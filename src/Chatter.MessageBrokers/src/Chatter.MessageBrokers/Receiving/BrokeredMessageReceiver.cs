using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
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
        private TransactionMode _transactionMode;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly IFailedReceiveRecoverer _failedReceiveRecoverer;
        private readonly ICriticalFailureNotifier _criticalFailureNotifier;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="infrastructureProvider">The message broker infrastructure</param>
        /// <param name="serviceFactory">The service scope factory used to create a new scope when a message is received from the messaging infrastructure.</param>
        /// <param name="logger">Provides logging capability</param>
        public BrokeredMessageReceiver(IMessagingInfrastructureProvider infrastructureProvider,
                                       ILogger<BrokeredMessageReceiver<TMessage>> logger,
                                       IServiceScopeFactory serviceFactory,
                                       IFailedReceiveRecoverer failedReceiveRecoverer,
                                       ICriticalFailureNotifier criticalFailureNotifier,
                                       ICircuitBreaker circuitBreaker)
        {
            if (infrastructureProvider is null)
            {
                throw new ArgumentNullException(nameof(infrastructureProvider));
            }

            _infrastructureProvider = infrastructureProvider;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _failedReceiveRecoverer = failedReceiveRecoverer;
            _criticalFailureNotifier = criticalFailureNotifier ?? throw new ArgumentNullException(nameof(criticalFailureNotifier));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
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
                await Start(options, receiverTerminationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogCritical($"Critical error occured curing {nameof(MessageReceiverLoopAsync)}: {e.Message}{Environment.NewLine}{e.StackTrace}");
            }
            return this;
        }

        async Task Start(ReceiverOptions options, CancellationToken receiverTerminationToken)
        {
            options.Description ??= options.MessageReceiverPath;
            _infrastructureReceiver = _infrastructureProvider.GetReceiver(options.InfrastructureType);
            options.MessageReceiverPath = _infrastructureProvider.GetInfrastructure(options.InfrastructureType).PathBuilder.GetMessageReceivingPath(options.SendingPath, options.MessageReceiverPath);

            if (options.TransactionMode != null)
            {
                _transactionMode = options.TransactionMode ?? TransactionMode.None;
            }

            _options = options;

            await _infrastructureReceiver.InitializeAsync(_options, receiverTerminationToken);

            this.SendingPath = options.SendingPath;
            this.MessageReceiverPath = options.MessageReceiverPath;
            this.ErrorQueueName = options.ErrorQueuePath;
            this.DeadLetterQueueName = options.DeadLetterQueuePath;

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
                    await _semaphore.WaitAsync(_messageReceiverLoopTokenSource.Token).ConfigureAwait(false);

                    try
                    {
                        await _circuitBreaker.Execute(async _ =>
                        {
                            MessageBrokerContext messageContext = null;
                            TransactionContext transactionContext = null;

                            using var receiverTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_messageReceiverLoopTokenSource.Token);

                            await ReceiveMessage(messageContext, transactionContext, receiverTokenSource);

                        }, _messageReceiverLoopTokenSource.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error receiving and handling azure service bus message");
                    }
                    finally
                    {
                        try
                        {
                            _semaphore.Release();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_messageReceiverLoopTokenSource.IsCancellationRequested)
            {
            }
        }

        async Task ReceiveMessage(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationTokenSource receiverTokenSource)
        {
            try
            {
                transactionContext = new TransactionContext(this.MessageReceiverPath, _transactionMode);
                using var infraTransaction = _infrastructureReceiver.BeginTransaction(transactionContext); //here for SSB, just before HandleInboundBrokeredMessage for ASB - will have to test this

                messageContext = await _infrastructureReceiver.ReceiveMessageAsync(transactionContext, receiverTokenSource.Token);

                if (messageContext == null || _messageReceiverLoopTokenSource.IsCancellationRequested)
                {
                    return;
                }

                await HandleInboundBrokeredMessage(messageContext, transactionContext);

                if (!receiverTokenSource.IsCancellationRequested)
                {
                    await _infrastructureReceiver.AckMessageAsync(messageContext, transactionContext, receiverTokenSource.Token);
                    _infrastructureReceiver?.CompleteTransaction(transactionContext);
                }
                else
                {
                    await _infrastructureReceiver.NackMessageAsync(messageContext, transactionContext, receiverTokenSource.Token);
                }

            }
            catch (PoisonedMessageException pme)
            {
                _logger?.LogError(pme, "Poisoned message received");
                try
                {
                    await _infrastructureReceiver.DeadletterMessageAsync(messageContext,
                                                                         transactionContext,
                                                                         pme.Message,
                                                                         pme.InnerException?.Message,
                                                                         receiverTokenSource.Token);
                }
                catch (Exception)
                {
                    _logger?.LogError("Error deadlettering poisoned message");
                    throw;
                }

                return;
            }
            catch (Exception e)
            {
                try
                {
                    _logger.LogError(e, $"Error handling recevied message. Attempting recovery. MessageId: '{messageContext.BrokeredMessage.MessageId}, CorrelationId: '{messageContext.BrokeredMessage.CorrelationId}'");

                    RecoveryState state = RecoveryState.Retrying;

                    using (var infraTransaction = _infrastructureReceiver.BeginTransaction(transactionContext))
                    {
                        var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                                this.ErrorQueueName,
                                                                "Unable to handle received message",
                                                                e,
                                                                await _infrastructureReceiver.CurrentMessageDeliveryCountAsync(messageContext, receiverTokenSource.Token),
                                                                transactionContext);

                        state = await _failedReceiveRecoverer.Execute(failureContext).ConfigureAwait(false);

                        if (state == RecoveryState.DeadLetter)
                        {
                            await _infrastructureReceiver.DeadletterMessageAsync(messageContext,
                                             transactionContext,
                                             $"Deadlettering message by request of recovery action.",
                                             $"MessageId: '{messageContext.BrokeredMessage.MessageId}, CorrelationId: '{messageContext.BrokeredMessage.CorrelationId}'",
                                             receiverTokenSource.Token);
                        }

                        if (state == RecoveryState.RecoveryActionExecuted)
                        {
                            await _infrastructureReceiver.AckMessageAsync(messageContext, transactionContext, receiverTokenSource.Token);
                        }

                        _infrastructureReceiver.CompleteTransaction(transactionContext);
                    }

                    if (state == RecoveryState.Retrying)
                    {
                        await _infrastructureReceiver.NackMessageAsync(messageContext, transactionContext, receiverTokenSource.Token);
                    }
                }
                catch (Exception onErrorException)
                {
                    var aggEx = new AggregateException(e, onErrorException);

                    _logger.LogError(aggEx, $"Recovery was unsuccessful. MessageId: '{messageContext.BrokeredMessage.MessageId}, CorrelationId: '{messageContext.BrokeredMessage.CorrelationId}'");

                    var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                            this.ErrorQueueName,
                                                            "Unable to recover from error which occurred during message handling",
                                                            aggEx,
                                                            await _infrastructureReceiver.CurrentMessageDeliveryCountAsync(messageContext, receiverTokenSource.Token),
                                                            transactionContext);

                    await _criticalFailureNotifier.Notify(failureContext).ConfigureAwait(false);

                    await _infrastructureReceiver.DeadletterMessageAsync(messageContext,
                                                                         transactionContext,
                                                                         $"Critical error encountered receiving message. MessageId: '{messageContext.BrokeredMessage.MessageId}, CorrelationId: '{messageContext.BrokeredMessage.MessageId}'",
                                                                         aggEx.ToString(),
                                                                         receiverTokenSource.Token);
                }
            }
        }

        public async Task StopReceiver()
        {
            _messageReceiverLoopTokenSource?.Cancel();

            if (_messageReceiverLoop != null && !_messageReceiverLoop.IsFaulted)
            {
                await _messageReceiverLoop.ConfigureAwait(false);
            }

            await _infrastructureReceiver.StopReceiver();

            _semaphore?.Dispose();
            _messageReceiverLoopTokenSource?.Dispose();
        }

        public virtual async Task HandleInboundBrokeredMessage(MessageBrokerContext messageContext, TransactionContext transactionContext)
        {
            try
            {
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

                    messageContext.Container.Include(transactionContext);

                    brokeredMessagePayload = inboundMessage.GetMessageFromBody<TMessage>();
                }
                catch (Exception e)
                {
                    throw new PoisonedMessageException($"Unable to build {typeof(MessageBrokerContext).Name} due to poisoned message", e);
                }

                using var scope = _serviceFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();
                messageContext.Container.Include((IExternalDispatcher)scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>());
                await dispatcher.Dispatch(brokeredMessagePayload, messageContext).ConfigureAwait(false);
            }
            catch (PoisonedMessageException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new ReceiverMessageDispatchingException($"Error dispatching message '{typeof(TMessage).Name}' received by '{typeof(BrokeredMessageReceiver<>).Name}'", e);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _infrastructureReceiver.DisposeAsync().ConfigureAwait(false);

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
