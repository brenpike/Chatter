using Chatter.CQRS;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Recovery;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// An infrastructure agnostic receiver of brokered messages of type <typeparamref name="TMessage"/>
    /// </summary>
    /// <typeparam name="TMessage">The type of messages the brokered message receiver accepts</typeparam>
    public class BrokeredMessageReceiver<TMessage> : IBrokeredMessageReceiver<TMessage> where TMessage : class, IMessage
    {
        private IMessagingInfrastructureReceiver _infrastructureReceiver;
        private readonly IMessagingInfrastructureProvider _infrastructureProvider;
        protected readonly ILogger<BrokeredMessageReceiver<TMessage>> _logger;
        protected ReceiverOptions _options;
        private bool _disposedValue;
        SemaphoreSlim _semaphore;
        CancellationTokenSource _messageReceiverLoopTokenSource;
        private Task _messageReceiverLoop;
        private readonly int _maxConcurrentCalls = 1; //TODO: add configuration for maxconcurrentcalls to messagebrokeroptions and/or receiveroptions
        private readonly MessageBrokerOptions _messageBrokerOptions;
        private readonly IRecoveryStrategy _recoveryStrategy;
        private readonly IReceivedMessageDispatcher _receivedMessageDispatcher;
        private readonly IMaxReceivesExceededAction _failedRecoveryAction;
        private readonly ICriticalFailureNotifier _criticalFailureNotifier;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="infrastructureProvider">The message broker infrastructure</param>
        /// <param name="serviceFactory">The service scope factory used to create a new scope when a message is received from the messaging infrastructure.</param>
        /// <param name="logger">Provides logging capability</param>
        public BrokeredMessageReceiver(IMessagingInfrastructureProvider infrastructureProvider,
                                       MessageBrokerOptions messageBrokerOptions,
                                       ILogger<BrokeredMessageReceiver<TMessage>> logger,
                                       IMaxReceivesExceededAction recoveryAction,
                                       ICriticalFailureNotifier criticalFailureNotifier,
                                       IRecoveryStrategy recoveryStrategy,
                                       IReceivedMessageDispatcher receivedMessageDispatcher)
        {
            if (infrastructureProvider is null)
            {
                throw new ArgumentNullException(nameof(infrastructureProvider));
            }

            _infrastructureProvider = infrastructureProvider;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _failedRecoveryAction = recoveryAction;
            _criticalFailureNotifier = criticalFailureNotifier ?? throw new ArgumentNullException(nameof(criticalFailureNotifier));
            _messageBrokerOptions = messageBrokerOptions ?? throw new ArgumentNullException(nameof(messageBrokerOptions));
            _recoveryStrategy = recoveryStrategy ?? throw new ArgumentNullException(nameof(recoveryStrategy));
            _receivedMessageDispatcher = receivedMessageDispatcher ?? throw new ArgumentNullException(nameof(receivedMessageDispatcher));
        }

        /// <summary>
        /// Indicates if the <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> is currently receiving messages
        /// </summary>
        public bool IsReceiving { get; private set; } = false;

        public string SendingPath { get; private set; }
        public string MessageReceiverPath { get; private set; }
        public string ErrorQueueName { get; private set; }
        public string DeadLetterQueueName { get; private set; }

        public Task<IAsyncDisposable> StartReceiver(ReceiverOptions options)
            => StartReceiver(options, CancellationToken.None);

        ///<inheritdoc/>
        public async Task<IAsyncDisposable> StartReceiver(ReceiverOptions options, CancellationToken receiverTerminationToken)
        {
            try
            {
                await InitAsync(options, receiverTerminationToken);
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, $"Critical unhandled error occured during {nameof(MessageReceiverLoopAsync)}");
            }

            return this;
        }

        async Task InitAsync(ReceiverOptions options, CancellationToken receiverTerminationToken)
        {
            _logger.LogInformation($"Initializing '{nameof(BrokeredMessageReceiver<TMessage>)}' of type '{typeof(TMessage).Name}'.");
            options.Description ??= options.MessageReceiverPath;
            _infrastructureReceiver = _infrastructureProvider.GetReceiver(options.InfrastructureType);
            options.MessageReceiverPath = _infrastructureProvider.GetInfrastructure(options.InfrastructureType).PathBuilder.GetMessageReceivingPath(options.SendingPath, options.MessageReceiverPath);

            this.SendingPath = options.SendingPath;
            this.MessageReceiverPath = options.MessageReceiverPath;
            this.ErrorQueueName = options.ErrorQueuePath;
            this.DeadLetterQueueName = options.DeadLetterQueuePath;

            options.TransactionMode ??= _messageBrokerOptions.TransactionMode;
            _options = options;

            _logger.LogTrace("Initializing messaging infrastructure");
            await _infrastructureReceiver.InitializeAsync(_options, receiverTerminationToken);
            _logger.LogTrace("Successfully initialized messaging infrastructure");

            _logger.LogDebug($"Receiver options: Infrastructure type: '{_options.InfrastructureType}', Transaction Mode: '{options.TransactionMode}', Message receiver: '{options.MessageReceiverPath}', Deadletter queue: '{options.DeadLetterQueuePath}', Error queue: '{options.ErrorQueuePath}', Max receive attempts: '{options.MaxReceiveAttempts}', Message sent from: '{options.SendingPath}', Max Concurrent Receives: '{_maxConcurrentCalls}'");

            _semaphore = new SemaphoreSlim(_maxConcurrentCalls, _maxConcurrentCalls);
            _messageReceiverLoopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(receiverTerminationToken);

            _messageReceiverLoop = MessageReceiverLoopAsync();
            this.IsReceiving = true;
            _logger.LogInformation($"'{nameof(BrokeredMessageReceiver<TMessage>)}' has started receiving messages of type '{typeof(TMessage).Name}'.");
            await _messageReceiverLoop;
            _logger.LogInformation($"'{nameof(BrokeredMessageReceiver<TMessage>)}' for messages of type '{typeof(TMessage).Name}' is shutting down.");
        }

        public async Task StopReceiver()
        {
            _messageReceiverLoopTokenSource?.Cancel();

            if (_messageReceiverLoop != null && !_messageReceiverLoop.IsFaulted)
            {
                await _messageReceiverLoop;
            }

            await _infrastructureReceiver.StopReceiver();

            _semaphore?.Dispose();
            _messageReceiverLoopTokenSource?.Dispose();
        }

        async Task MessageReceiverLoopAsync()
        {
            try
            {
                while (!_messageReceiverLoopTokenSource.IsCancellationRequested)
                {
                    await _semaphore.WaitAsync(_messageReceiverLoopTokenSource.Token);

                    MessageBrokerContext messageContext = null;
                    TransactionContext transactionContext = new TransactionContext(this.MessageReceiverPath, _options.TransactionMode.Value);

                    try
                    {
                        _messageReceiverLoopTokenSource.Token.ThrowIfCancellationRequested();

                        messageContext = await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.ReceiveMessageAsync(transactionContext, _messageReceiverLoopTokenSource.Token), _messageReceiverLoopTokenSource.Token);

                        if (messageContext != null)
                        {
                            _logger.LogTrace("Message received successfully");
                            await ProcessMessageAsync(messageContext, transactionContext, _messageReceiverLoopTokenSource.Token);
                            _logger.LogTrace("Message processed successfully");
                        }
                    }
                    catch (CriticalReceiverException)
                    {
                        throw; //stop receiver loop
                    }
                    catch (OperationCanceledException) when (_messageReceiverLoopTokenSource.IsCancellationRequested)
                    {
                    }
                    catch (ObjectDisposedException) when (_messageReceiverLoopTokenSource.IsCancellationRequested)
                    {
                    }
                    catch (PoisonedMessageException e)
                    {
                        _logger.LogError(e, $"Poisoned message received. Deadlettering.");
                        await TryDeadletterWithRecoveryAsync(messageContext, transactionContext, e, _messageReceiverLoopTokenSource.Token);
                    }
                    catch (Exception e)
                    {
                        if (messageContext == null)
                        {
                            _logger.LogError(e, "Error receiving brokered message");
                        }
                        else
                        {
                            _logger.LogError(e, "Error processing brokered message");
                            var deliveryCount = await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.MessageDeliveryCountAsync(messageContext, _messageReceiverLoopTokenSource.Token), _messageReceiverLoopTokenSource.Token);
                            if (deliveryCount >= _options.MaxReceiveAttempts)
                            {
                                if (await TryDeadletterWithRecoveryAsync(messageContext, transactionContext, e, _messageReceiverLoopTokenSource.Token))
                                {
                                    await TryExecuteFailedRecoveryAction(messageContext, "Max message receive attempts exceeded", e, deliveryCount, transactionContext);
                                }
                            }
                            else
                            {
                                await TryNackWithRecoveryAsync(messageContext, transactionContext, _messageReceiverLoopTokenSource.Token);
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            _semaphore?.Release();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                }
            }
            catch (CriticalReceiverException e)
            {
                _logger.LogCritical(e, "Receiver is unable continue due to critical error");
                await _criticalFailureNotifier.Notify(new FailureContext(null, this.ErrorQueueName, "Critical error occurred", e, -1, null));
            }
        }

        public virtual async Task DispatchReceivedMessageAsync(TMessage payload, MessageBrokerContext messageContext, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            try
            {
                await _receivedMessageDispatcher.DispatchAsync(payload, messageContext, receiverTokenSource);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error dispatching brokered message to handler(s)");
                throw;
            }
        }

        async Task ProcessMessageAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            TMessage brokeredMessagePayload = null;

            var inboundMessage = messageContext.BrokeredMessage;

            if (transactionContext is null)
            {
                transactionContext = new TransactionContext(_options.MessageReceiverPath, _options.TransactionMode.Value);
            }

            messageContext.Container.GetOrAdd(() => transactionContext);

            try
            {
                brokeredMessagePayload = inboundMessage.GetMessageFromBody<TMessage>();
            }
            catch (Exception e)
            {
                throw new PoisonedMessageException($"Unable deserialize {typeof(TMessage).Name} from message body", e);
            }

            using var localTransaction = _infrastructureReceiver.CreateLocalTransaction(transactionContext);
            await _recoveryStrategy.ExecuteAsync(async () =>
                {
                    await DispatchReceivedMessageAsync(brokeredMessagePayload, messageContext, receiverTokenSource);
                    return Task.FromResult(true);
                }, receiverTokenSource);

            if (!receiverTokenSource.IsCancellationRequested)
            {
                if (await TryAckWithRecoveryAsync(messageContext, transactionContext, receiverTokenSource))
                {
                    localTransaction?.Complete();
                }
            }
            else
            {
                await TryNackWithRecoveryAsync(messageContext, transactionContext, receiverTokenSource);
            }
        }

        private async Task<bool> TryAckWithRecoveryAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken receiverTokenSource)
        {
            try
            {
                return await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.AckMessageAsync(messageContext, transactionContext, receiverTokenSource), receiverTokenSource);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to send acknowledgment");
                return false;
            }
        }

        private async Task<bool> TryNackWithRecoveryAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken receiverTokenSource)
        {
            try
            {
                return await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.NackMessageAsync(messageContext, transactionContext, receiverTokenSource), receiverTokenSource);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to send negative acknowledgment");
                return false;
            }
        }

        private async Task<bool> TryDeadletterWithRecoveryAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, Exception e, CancellationToken receiverTokenSource)
        {
            try
            {
                return await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.DeadletterMessageAsync(messageContext, transactionContext, "Poisoned message received", e.ToString(), receiverTokenSource), receiverTokenSource);
            }
            catch (Exception inner)
            {
                _logger.LogError(inner, "Unable to deadletter message");
                return false;
            }
        }

        private async Task<bool> TryExecuteFailedRecoveryAction(MessageBrokerContext messageContext, string failureDescription, Exception exception, int deliveryCount, TransactionContext transactionContext)
        {
            try
            {
                var failureContext = new FailureContext(messageContext.BrokeredMessage, this.ErrorQueueName, failureDescription, exception, deliveryCount, transactionContext);
                return await _recoveryStrategy.ExecuteAsync(() =>
                {
                    _failedRecoveryAction.ExecuteAsync(failureContext);
                    return Task.FromResult(true);
                }, messageContext.CancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to execute recovery action");
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _infrastructureReceiver.DisposeAsync();

            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _messageReceiverLoopTokenSource?.Cancel();
                    _infrastructureReceiver?.Dispose();
                    _semaphore?.Dispose();
                    _messageReceiverLoopTokenSource?.Dispose();
                }

                _infrastructureReceiver = null;
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
