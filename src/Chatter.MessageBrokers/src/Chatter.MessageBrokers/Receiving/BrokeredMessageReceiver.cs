using Chatter.CQRS;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

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
        protected readonly IServiceScopeFactory _serviceFactory;
        protected ReceiverOptions _options;
        private bool _disposedValue;
        SemaphoreSlim _semaphore;
        CancellationTokenSource _messageReceiverLoopTokenSource;
        private Task _messageReceiverLoop;
        private readonly int _maxConcurrentCalls = 1;
        private readonly MessageBrokerOptions _messageBrokerOptions;
        private readonly IRecoveryStrategy _recoveryStrategy;
        private readonly IMaxReceivesExceededAction _recoveryAction;
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
                                       IServiceScopeFactory serviceFactory,
                                       IMaxReceivesExceededAction recoveryAction,
                                       ICriticalFailureNotifier criticalFailureNotifier,
                                       IRecoveryStrategy recoveryStrategy)
        {
            if (infrastructureProvider is null)
            {
                throw new ArgumentNullException(nameof(infrastructureProvider));
            }

            _infrastructureProvider = infrastructureProvider;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _recoveryAction = recoveryAction;
            _criticalFailureNotifier = criticalFailureNotifier ?? throw new ArgumentNullException(nameof(criticalFailureNotifier));
            _messageBrokerOptions = messageBrokerOptions ?? throw new ArgumentNullException(nameof(messageBrokerOptions));
            _recoveryStrategy = recoveryStrategy ?? throw new ArgumentNullException(nameof(recoveryStrategy));
            //add configuration for maxconcurrentcalls to messagebrokeroptions and/or receiveroptions
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

                        messageContext = await _recoveryStrategy.ExecuteAsync(
                                () => _infrastructureReceiver.ReceiveMessageAsync(transactionContext, _messageReceiverLoopTokenSource.Token),
                                _messageReceiverLoopTokenSource.Token
                            );

                        if (messageContext != null)
                        {
                            await HandleMessage(messageContext, transactionContext, _messageReceiverLoopTokenSource.Token);
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
                    catch (PoisonedMessageException pme)
                    {
                        var msg = "Poisoned message was received by brokered message receiver";
                        _logger.LogError(pme, msg);

                        try
                        {
                            await _recoveryStrategy.ExecuteAsync(
                                    () => _infrastructureReceiver.DeadletterMessageAsync(messageContext, transactionContext, msg, pme.ToString(), _messageReceiverLoopTokenSource.Token),
                                    _messageReceiverLoopTokenSource.Token
                                );
                        }
                        catch (Exception e)
                        {
                            var agEx = new AggregateException(pme, e);
                            _logger.LogError(agEx, "Error deadlettering poisoned message");
                        }
                    }
                    catch (Exception e) when (messageContext != null)
                    {
                        _logger.LogError(e, "Error receiving brokered message");
                        try
                        {
                            var deliveryCount = await _recoveryStrategy.ExecuteAsync(
                                    () => _infrastructureReceiver.MessageDeliveryCountAsync(messageContext, _messageReceiverLoopTokenSource.Token),
                                    _messageReceiverLoopTokenSource.Token
                                );
                            if (deliveryCount >= _options.MaxReceiveAttempts)
                            {
                                await _recoveryStrategy.ExecuteAsync(
                                        () => _infrastructureReceiver.DeadletterMessageAsync(messageContext, transactionContext, "Max message receive attempts exceeded", e.ToString(), _messageReceiverLoopTokenSource.Token),
                                        _messageReceiverLoopTokenSource.Token
                                    );

                                //todo: retry?
                                await _recoveryAction.ExecuteAsync(new FailureContext(messageContext.BrokeredMessage, this.ErrorQueueName, "Max message receive attempts exceeded", e, deliveryCount, transactionContext));
                            }
                            else
                            {
                                await _recoveryStrategy.ExecuteAsync(
                                        () => _infrastructureReceiver.NackMessageAsync(messageContext, transactionContext, _messageReceiverLoopTokenSource.Token),
                                        _messageReceiverLoopTokenSource.Token
                                    );
                            }
                        }
                        catch (Exception inner)
                        {
                            var agEx = new AggregateException(e, inner);
                            _logger.LogError(agEx, "Unable to deadletter or send negative acknowledgment");
                        }
                    }
                    catch (Exception e) when (messageContext == null)
                    {
                        //TODO: this could be an infinite loop since we can't check for _options.MaxReceiveAttempts since messageContext is null...how do we stop?
                        _logger.LogError(e, $"Error receiving brokered message. Null {nameof(MessageBrokerContext)} received.");
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

        public virtual async Task DispatchReceivedMessage(TMessage payload, MessageBrokerContext messageContext, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            try
            {
                using var scope = _serviceFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();
                messageContext.Container.Include((IExternalDispatcher)scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>());
                await dispatcher.Dispatch(payload, messageContext);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error dispatching brokered message to handler(s)");
                throw;
            }
        }

        async Task HandleMessage(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            TMessage brokeredMessagePayload = null;

            try
            {
                var inboundMessage = messageContext.BrokeredMessage;

                if (transactionContext is null)
                {
                    transactionContext = new TransactionContext(_options.MessageReceiverPath, _options.TransactionMode.Value);
                }

                messageContext.Container.GetOrAdd(() => transactionContext);

                brokeredMessagePayload = inboundMessage.GetMessageFromBody<TMessage>();
            }
            catch (Exception e)
            {
                throw new PoisonedMessageException($"Unable to construct {nameof(InboundBrokeredMessage)} due to poisoned message", e);
            }
            
            using TransactionScope localTransaction = _infrastructureReceiver.CreateLocalTransaction(transactionContext);
            await DispatchReceivedMessage(brokeredMessagePayload, messageContext, receiverTokenSource);

            if (!receiverTokenSource.IsCancellationRequested)
            {
                await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.AckMessageAsync(messageContext, transactionContext, receiverTokenSource), receiverTokenSource);
                localTransaction?.Complete();
            }
            else
            {
                await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.NackMessageAsync(messageContext, transactionContext, receiverTokenSource), receiverTokenSource);
            }
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
