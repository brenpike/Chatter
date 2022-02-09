using Chatter.CQRS;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;
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
        protected readonly IServiceScopeFactory _serviceFactory;
        protected ReceiverOptions _options;
        private bool _disposedValue;
        SemaphoreSlim _semaphore;
        CancellationTokenSource _messageReceiverLoopTokenSource;
        private Task _messageReceiverLoop;
        private readonly int _maxConcurrentCalls = 1;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly MessageBrokerOptions _messageBrokerOptions;
        private readonly IRetryStrategy _retryRecovery;
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
                _logger.LogCritical($"Critical error occured curing {nameof(MessageReceiverLoopAsync)}: {e.Message}{Environment.NewLine}{e.StackTrace}");
            }

            return this;
        }

        async Task InitAsync(ReceiverOptions options, CancellationToken receiverTerminationToken)
        {
            options.Description ??= options.MessageReceiverPath;
            _infrastructureReceiver = _infrastructureProvider.GetReceiver(options.InfrastructureType);
            options.MessageReceiverPath = _infrastructureProvider.GetInfrastructure(options.InfrastructureType).PathBuilder.GetMessageReceivingPath(options.SendingPath, options.MessageReceiverPath);

            this.SendingPath = options.SendingPath;
            this.MessageReceiverPath = options.MessageReceiverPath;
            this.ErrorQueueName = options.ErrorQueuePath;
            this.DeadLetterQueueName = options.DeadLetterQueuePath;

            options.TransactionMode ??= _messageBrokerOptions.TransactionMode;

            _options = options;

            await _infrastructureReceiver.InitializeAsync(_options, receiverTerminationToken);

            _semaphore = new SemaphoreSlim(_maxConcurrentCalls, _maxConcurrentCalls);
            _messageReceiverLoopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(receiverTerminationToken);

            _messageReceiverLoop = MessageReceiverLoopAsync();

            _logger.LogInformation($"'{GetType().FullName}' has started receiving messages.");

            await _messageReceiverLoop;
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
                        using var receiverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_messageReceiverLoopTokenSource.Token);
                        messageContext = await ReceiveMessage(transactionContext, receiverTokenSource.Token);

                        if (messageContext != null)
                        {
                            await HandleMessage(messageContext, transactionContext, receiverTokenSource.Token);
                        }
                    }
                    catch (CriticalReceiverException)
                    {
                        throw;
                    }
                    catch (Exception e) when (messageContext != null)
                    {
                        if (await _infrastructureReceiver.MessageDeliveryCountAsync(messageContext, _messageReceiverLoopTokenSource.Token) >= _options.MaxReceiveAttempts)
                        {
                            await _recoveryStrategy.ExecuteAsync(
                                    () => _infrastructureReceiver.DeadletterMessageAsync(messageContext, transactionContext, "Max message receive attempts exceeded", e.ToString(), _messageReceiverLoopTokenSource.Token),
                                    _messageReceiverLoopTokenSource.Token
                                );
                            //TODO: execute recovery action
                        }
                        else
                        {
                            await _recoveryStrategy.ExecuteAsync(
                                    () => _infrastructureReceiver.NackMessageAsync(messageContext, transactionContext, _messageReceiverLoopTokenSource.Token),
                                    _messageReceiverLoopTokenSource.Token
                                );
                        }
                        _logger.LogError(e, "Error receiving and handling brokered message");
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error receiving and handling brokered message");
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
                //_criticalFailureNotifier.Notify(...);
            }
            catch (OperationCanceledException) when (_messageReceiverLoopTokenSource.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_messageReceiverLoopTokenSource.IsCancellationRequested)
            {
            }
        }

        /// <summary>
        /// Receives a message from the message broker infrastructure.
        /// </summary>
        /// <param name="receiverTokenSource"></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        /// <remarks>
        /// If <see cref="ReceiveMessage(CancellationToken)"/> returns, the <see cref="ICircuitBreaker"/> will not be triggered.
        /// If an <see cref="Exception"/> is thrown, the circuit breaker will be triggered.
        /// </remarks>
        async Task<MessageBrokerContext> ReceiveMessage(TransactionContext transactionContext, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            MessageBrokerContext messageContext;

            try
            {
                messageContext = await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.ReceiveMessageAsync(transactionContext, receiverTokenSource), receiverTokenSource);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to receive message from infrastructure");
                throw;
            }

            return messageContext;
        }

        public virtual Task DispatchReceivedMessage(TMessage payload, MessageBrokerContext messageContext, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            try
            {
                using var scope = _serviceFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();
                messageContext.Container.Include((IExternalDispatcher)scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>());
                return dispatcher.Dispatch(payload, messageContext);
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

            try
            {
                using var ts = _infrastructureReceiver.CreateLocalTransaction(transactionContext);

                TMessage brokeredMessagePayload = null;

                try
                {
                    if (messageContext is null)
                    {
                        throw new ArgumentNullException(nameof(messageContext), $"A {typeof(MessageBrokerContext).Name} was not created by the messaging infrastructure.");
                    }

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

                await DispatchReceivedMessage(brokeredMessagePayload, messageContext, receiverTokenSource);

                if (!receiverTokenSource.IsCancellationRequested)
                {
                    await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.AckMessageAsync(messageContext, transactionContext, receiverTokenSource), receiverTokenSource);
                    ts?.Complete();
                }
                else
                {
                    await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.NackMessageAsync(messageContext, transactionContext, receiverTokenSource), receiverTokenSource);
                }
            }
            catch (PoisonedMessageException pme)
            {
                try
                {
                    var msg = "Poisoned message was received by brokered message receiver";
                    await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.DeadletterMessageAsync(messageContext, transactionContext, msg, pme.ToString(), receiverTokenSource), receiverTokenSource);
                    _logger?.LogError(pme, msg);
                }
                catch (Exception e)
                {
                    _logger?.LogError(e, "Error deadlettering poisoned message");
                    throw;
                }
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
