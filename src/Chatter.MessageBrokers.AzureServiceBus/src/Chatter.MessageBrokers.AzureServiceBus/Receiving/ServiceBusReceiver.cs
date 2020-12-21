using Chatter.MessageBrokers.AzureServiceBus.Extensions;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Azure.ServiceBus.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    public class ServiceBusReceiver : IMessagingInfrastructureReceiver
    {
        readonly object _syncLock;
        private readonly ILogger<ServiceBusReceiver> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IFailedReceiveRecoverer _failedReceiveRecoverer;
        private readonly ICriticalFailureNotifier _criticalFailureNotifier;
        private readonly ITokenProvider _tokenProvider;
        private readonly int _maxConcurrentCalls = 1;
        private readonly RetryPolicy _retryPolcy;
        private readonly int _prefetchCount;
        private ReceiveMode _receiveMode;
        MessageReceiver _innerReceiver;
        SemaphoreSlim _semaphore;
        CancellationTokenSource _messageReceiverLoopTokenSource;
        private Task _messageReceiverLoop;
        private bool _disposedValue;
        private TransactionMode _transactionMode;

        public ServiceBusReceiver(ServiceBusOptions serviceBusOptions,
                                  MessageBrokerOptions messageBrokerOptions,
                                  ILogger<ServiceBusReceiver> logger,
                                  IBodyConverterFactory bodyConverterFactory,
                                  IFailedReceiveRecoverer failedReceiveRecoverer,
                                  ICriticalFailureNotifier criticalFailureNotifier)
        {
            if (serviceBusOptions is null)
            {
                throw new ArgumentNullException(nameof(serviceBusOptions));
            }

            _syncLock = new object();
            ServiceBusConnectionBuilder = new ServiceBusConnectionStringBuilder(serviceBusOptions.ConnectionString);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _failedReceiveRecoverer = failedReceiveRecoverer;
            _criticalFailureNotifier = criticalFailureNotifier ?? throw new ArgumentNullException(nameof(criticalFailureNotifier));
            _tokenProvider = serviceBusOptions.TokenProvider;
            _maxConcurrentCalls = serviceBusOptions.MaxConcurrentCalls;
            _retryPolcy = serviceBusOptions.Policy;
            _prefetchCount = serviceBusOptions.PrefetchCount;
            _transactionMode = messageBrokerOptions?.TransactionMode ?? TransactionMode.None;
            _receiveMode = messageBrokerOptions?.TransactionMode == TransactionMode.None ? ReceiveMode.ReceiveAndDelete : ReceiveMode.PeekLock;
        }
        /// <summary>
        /// Connection object to the service bus namespace.
        /// </summary>
        public ServiceBusConnectionStringBuilder ServiceBusConnectionBuilder { get; }

        public string MessageReceiverPath { get; private set; }
        public string ErrorQueuePath { get; private set; }

        internal MessageReceiver InnerReceiver
        {
            get
            {
                if (_innerReceiver == null)
                {
                    lock (_syncLock)
                    {
                        if (_innerReceiver == null)
                        {
                            if (_tokenProvider is NullTokenProvider)
                            {
                                _innerReceiver = new MessageReceiver(this.ServiceBusConnectionBuilder.GetNamespaceConnectionString(),
                                                                     this.MessageReceiverPath,
                                                                     _receiveMode,
                                                                     _retryPolcy,
                                                                     _prefetchCount);
                            }
                            else
                            {
                                _innerReceiver = new MessageReceiver(this.ServiceBusConnectionBuilder.Endpoint,
                                                                     this.MessageReceiverPath,
                                                                     _tokenProvider,
                                                                     this.ServiceBusConnectionBuilder.TransportType,
                                                                     _receiveMode,
                                                                     _retryPolcy,
                                                                     _prefetchCount);
                            }
                        }
                    }
                }

                return _innerReceiver;
            }
        }

        public async Task StartReceiver(ReceiverOptions options,
                                        Func<MessageBrokerContext, TransactionContext, Task> brokeredMessageHandler)
        {
            this.MessageReceiverPath = options.MessageReceiverPath;
            this.ErrorQueuePath = options.ErrorQueuePath;

            if (options.TransactionMode != null)
            {
                _transactionMode = options.TransactionMode ?? TransactionMode.None;
                _receiveMode = _transactionMode == TransactionMode.None ? ReceiveMode.ReceiveAndDelete : ReceiveMode.PeekLock;
            }

            _semaphore = new SemaphoreSlim(_maxConcurrentCalls, _maxConcurrentCalls);
            _messageReceiverLoopTokenSource = new CancellationTokenSource();

            try
            {
                _messageReceiverLoop = MessageReceiverLoop(brokeredMessageHandler);
                await _messageReceiverLoop.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogCritical($"Critical error occured curing {nameof(MessageReceiverLoop)}: {e.Message}{Environment.NewLine}{e.StackTrace}");
            }
        }

        public async Task StopReceiver()
        {
            _messageReceiverLoopTokenSource?.Cancel();

            if (_messageReceiverLoop != null && !_messageReceiverLoop.IsFaulted)
            {
                await _messageReceiverLoop.ConfigureAwait(false);
            }

            if (_innerReceiver != null)
            {
                await _innerReceiver.CloseAsync().ConfigureAwait(false);
            }

            _semaphore?.Dispose();
            _messageReceiverLoopTokenSource?.Dispose();
        }

        async Task MessageReceiverLoop(Func<MessageBrokerContext, TransactionContext, Task> brokeredMessageHandler)
        {
            try
            {
                while (!_messageReceiverLoopTokenSource.IsCancellationRequested)
                {
                    await _semaphore.WaitAsync(_messageReceiverLoopTokenSource.Token).ConfigureAwait(false);

                    try
                    {
                        Message message = null;

                        try
                        {
                            message = await this.InnerReceiver.ReceiveAsync();
                        }
                        catch (ServiceBusException sbe) when (sbe.IsTransient)
                        {
                            //TODO: throw? do something special?
                        }
                        catch (Exception)
                        {
                            //TODO: log critical and return ending loop???
                            _logger.LogError("Failure to receive message from Azure Serivce Bus");
                            throw;
                        }

                        if (message == null || _messageReceiverLoopTokenSource.IsCancellationRequested)
                        {
                            continue;
                        }

                        MessageBrokerContext messageContext = null;
                        TransactionContext transactionContext = null;

                        try
                        {
                            using var receiverTokenSource = new CancellationTokenSource();

                            try
                            {
                                message.AddUserProperty(MessageContext.TimeToLive, message.TimeToLive);
                                message.AddUserProperty(MessageContext.ExpiryTimeUtc, message.ExpiresAtUtc);
                                message.AddUserProperty(MessageContext.InfrastructureType, ASBMessageContext.InfrastructureType);

                                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(message.ContentType);

                                messageContext = new MessageBrokerContext(message.MessageId, message.Body, message.UserProperties, this.MessageReceiverPath, receiverTokenSource.Token, bodyConverter);
                                messageContext.Container.Include(message);

                                transactionContext = new TransactionContext(this.MessageReceiverPath, _transactionMode);
                                transactionContext.Container.Include(this.InnerReceiver);

                                if (_transactionMode == TransactionMode.FullAtomicityViaInfrastructure)
                                {
                                    transactionContext.Container.Include(this.InnerReceiver.ServiceBusConnection);
                                }
                            }
                            catch (Exception e)
                            {
                                throw new PoisonedMessageException($"Unable to build {typeof(MessageBrokerContext).Name} due to poisoned message", e);
                            }

                            using var scope = CreateTransactionScope(_transactionMode);
                            await brokeredMessageHandler(messageContext, transactionContext).ConfigureAwait(false);

                            if (!receiverTokenSource.IsCancellationRequested)
                            {
                                await CompleteAsync(message).ConfigureAwait(false);
                                scope?.Complete();
                            }
                            else
                            {
                                await AbandonAsync(message).ConfigureAwait(false);
                            }
                        }
                        catch (PoisonedMessageException pme)
                        {
                            _logger?.LogError(pme, "Poisoned message received");
                            try
                            {
                                await DeadLetterAsync(message, pme.Message, pme.InnerException?.Message).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                _logger?.LogError("Error deadlettering poisoned message");
                                throw;
                            }

                            continue;
                        }
                        catch (Exception e)
                        {
                            try
                            {
                                _logger.LogError(e, "Error handling recevied message. Attempting recovery. MessageId: '{message.MessageId}, CorrelationId: '{message.CorrelationId}'");

                                RecoveryState state = RecoveryState.Retrying;

                                using (var scope = CreateTransactionScope(_transactionMode))
                                {
                                    var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                                            this.ErrorQueuePath,
                                                                            "Unable to handle received message",
                                                                            e,
                                                                            message.SystemProperties.DeliveryCount,
                                                                            transactionContext);

                                    state = await _failedReceiveRecoverer.Execute(failureContext).ConfigureAwait(false);

                                    if (state == RecoveryState.DeadLetter)
                                    {
                                        await DeadLetterAsync(message,
                                                              $"Deadlettering message by request of recovery action.",
                                                              $"MessageId: '{message.MessageId}, CorrelationId: '{message.CorrelationId}'").ConfigureAwait(false);
                                    }

                                    if (state == RecoveryState.RecoveryActionExecuted)
                                    {
                                        await CompleteAsync(message).ConfigureAwait(false);
                                    }

                                    scope?.Complete();
                                }

                                if (state == RecoveryState.Retrying)
                                {
                                    await AbandonAsync(message).ConfigureAwait(false);
                                }
                            }
                            catch (Exception onErrorException) when (onErrorException is MessageLockLostException || onErrorException is ServiceBusTimeoutException)
                            {
                                _logger.LogError(onErrorException, "Unable to recover from error that occurred during message receiving");
                            }
                            catch (Exception onErrorException)
                            {
                                var aggEx = new AggregateException(e, onErrorException);

                                _logger.LogError(aggEx, $"Recovery was unsuccessful. MessageId: '{message.MessageId}, CorrelationId: '{message.CorrelationId}'");

                                var failureContext = new FailureContext(messageContext.BrokeredMessage,
                                                                        this.ErrorQueuePath,
                                                                        "Unable to recover from error which occurred during message handling",
                                                                        aggEx,
                                                                        message.SystemProperties.DeliveryCount,
                                                                        transactionContext);

                                await _criticalFailureNotifier.Notify(failureContext).ConfigureAwait(false);

                                await DeadLetterAsync(message,
                                                      $"Critical error encountered receiving message. MessageId: '{message.MessageId}, CorrelationId: '{message.CorrelationId}'",
                                                      aggEx.ToString()).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        //TODO: circuit breaker
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
            catch (OperationCanceledException)
            {
            }
        }

        private Task CompleteAsync(Message msg)
        {
            if (_transactionMode == TransactionMode.None)
            {
                return Task.CompletedTask;
            }

            return this.InnerReceiver.CompleteAsync(msg.SystemProperties.LockToken);
        }

        private Task AbandonAsync(Message msg)
        {
            if (_transactionMode == TransactionMode.None)
            {
                return Task.CompletedTask;
            }

            return this.InnerReceiver.AbandonAsync(msg.SystemProperties.LockToken, msg.UserProperties);
        }

        private Task DeadLetterAsync(Message msg, string deadLetterReason, string deadLetterErrorDescription)
        {
            if (_transactionMode == TransactionMode.None)
            {
                return Task.CompletedTask;
            }

            return this.InnerReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, deadLetterReason, deadLetterErrorDescription);
        }

        TransactionScope CreateTransactionScope(TransactionMode transactionMode)
        {
            if (transactionMode == TransactionMode.None || transactionMode == TransactionMode.ReceiveOnly)
            {
                return null;
            }
            else
            {
                return new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopReceiver().ConfigureAwait(false);

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
                    _innerReceiver?.CloseAsync();
                    _semaphore?.Dispose();
                    _messageReceiverLoopTokenSource?.Dispose();
                }

                _innerReceiver = null;
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
