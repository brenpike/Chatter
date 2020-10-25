using Chatter.MessageBrokers.AzureServiceBus.Extensions;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
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
        private readonly ITokenProvider _tokenProvider;
        private readonly int _maxConcurrentCalls = 1;
        private readonly RetryPolicy _retryPolcy;
        private readonly int _prefetchCount;
        private readonly MessageBrokerOptions _messageBrokerOptions;
        private readonly ReceiveMode _receiveMode;
        MessageReceiver _innerReceiver;
        SemaphoreSlim _semaphore;
        CancellationTokenSource _messageReceiverLoopTokenSource;
        private Task _messageReceiverLoop;

        public ServiceBusReceiver(ServiceBusOptions serviceBusConfiguration,
                                  MessageBrokerOptions messageBrokerOptions,
                                  ILogger<ServiceBusReceiver> logger,
                                  IBodyConverterFactory bodyConverterFactory)
        {
            if (serviceBusConfiguration is null)
            {
                throw new ArgumentNullException(nameof(serviceBusConfiguration));
            }

            _syncLock = new object();
            ServiceBusConnectionBuilder = new ServiceBusConnectionStringBuilder(serviceBusConfiguration.ConnectionString);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _tokenProvider = serviceBusConfiguration.TokenProvider;
            _maxConcurrentCalls = serviceBusConfiguration.MaxConcurrentCalls;
            _retryPolcy = serviceBusConfiguration.Policy;
            _prefetchCount = serviceBusConfiguration.PrefetchCount;
            _messageBrokerOptions = messageBrokerOptions;
            _receiveMode = messageBrokerOptions?.TransactionMode == TransactionMode.None ? ReceiveMode.ReceiveAndDelete : ReceiveMode.PeekLock;
        }
        /// <summary>
        /// Connection object to the service bus namespace.
        /// </summary>
        public ServiceBusConnectionStringBuilder ServiceBusConnectionBuilder { get; }

        public string MessageReceiverPath { get; private set; }

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

        public Task StartReceiver(string receiverPath,
                                  Func<MessageBrokerContext, TransactionContext, Task> brokeredMessageHandler)
        {
            this.MessageReceiverPath = receiverPath;

            _semaphore = new SemaphoreSlim(_maxConcurrentCalls, _maxConcurrentCalls);
            _messageReceiverLoopTokenSource = new CancellationTokenSource();

            _messageReceiverLoop = MessageReceiverLoop(brokeredMessageHandler);

            return _messageReceiverLoop;
        }

        public async Task StopReceiver()
        {
            _messageReceiverLoopTokenSource?.Cancel();

            if (_messageReceiverLoop != null)
            {
                await _messageReceiverLoop.ConfigureAwait(false);
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

                    var message = await this.InnerReceiver.ReceiveAsync();

                    if (message == null || _messageReceiverLoopTokenSource.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        try
                        {
                            using var receiverTokenSource = new CancellationTokenSource();

                            var transactionMode = _messageBrokerOptions?.TransactionMode ?? TransactionMode.None;

                            MessageBrokerContext messageContext = null;
                            TransactionContext transactionContext = null;

                            try
                            {
                                message.AddUserProperty(MessageContext.TimeToLive, message.TimeToLive);
                                message.AddUserProperty(MessageContext.ExpiryTimeUtc, message.ExpiresAtUtc);

                                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(message.ContentType);

                                messageContext = new MessageBrokerContext(message.MessageId, message.Body, message.UserProperties, this.MessageReceiverPath, receiverTokenSource.Token, bodyConverter);
                                messageContext.Container.Include(message);

                                transactionContext = new TransactionContext(this.MessageReceiverPath, transactionMode);
                                transactionContext.Container.Include(this.InnerReceiver);

                                if (transactionMode == TransactionMode.FullAtomicityViaInfrastructure)
                                {
                                    transactionContext.Container.Include(this.InnerReceiver.ServiceBusConnection);
                                }
                            }
                            catch (Exception e)
                            {
                                throw new PoisonedMessageException($"Unable to build {typeof(MessageBrokerContext).Name} due to poisoned message", e);
                            }

                            using var scope = CreateTransactionScope(transactionMode);
                            await brokeredMessageHandler(messageContext, transactionContext).ConfigureAwait(false);

                            if (!receiverTokenSource.IsCancellationRequested)
                            {
                                await CompleteAsync(message).ConfigureAwait(false);
                                scope?.Complete();
                            }
                            else
                            {
                                await AbandonWithExponentialDelayAsync(message).ConfigureAwait(false);
                            }
                        }
                        catch (PoisonedMessageException pme)
                        {
                            try
                            {
                                await DeadLetterAsync(message, pme.Message, pme.InnerException?.Message).ConfigureAwait(false);
                            }
                            catch (Exception deadLetterException)
                            {
                                _logger?.LogError($"Error deadlettering message: {deadLetterException.StackTrace}");
                            }

                            return;
                        }
                        catch (CriticalBrokeredMessageReceiverException cbmre)
                        {
                            await AbandonWithExponentialDelayAsync(message).ConfigureAwait(false);
                            _logger.LogError($"Critical error encountered receiving message. MessageId: '{message.MessageId}, CorrelationId: '{message.CorrelationId}'"
                                           + "\n"
                                           + $"{cbmre.ErrorContext}");
                        }
                        catch (Exception e)
                        {
                            await AbandonWithExponentialDelayAsync(message).ConfigureAwait(false);
                            _logger.LogError($"Message with Id '{message.MessageId}' has been abandoned:\n '{e}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error processing {typeof(Message).Name} with id '{message.MessageId}' and correlation id '{message.CorrelationId}': {ex.StackTrace}");
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
            if (_messageBrokerOptions.TransactionMode == TransactionMode.None)
            {
                return Task.CompletedTask;
            }

            return this.InnerReceiver.CompleteAsync(msg.SystemProperties.LockToken);
        }

        private Task AbandonAsync(Message msg)
        {
            if (_messageBrokerOptions.TransactionMode == TransactionMode.None)
            {
                return Task.CompletedTask;
            }

            return this.InnerReceiver.AbandonAsync(msg.SystemProperties.LockToken, msg.UserProperties);
        }

        private Task DeadLetterAsync(Message msg, string deadLetterReason, string deadLetterErrorDescription)
        {
            if (_messageBrokerOptions.TransactionMode == TransactionMode.None)
            {
                return Task.CompletedTask;
            }

            return this.InnerReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, deadLetterReason, deadLetterErrorDescription);
        }

        private async Task AbandonWithExponentialDelayAsync(Message msg)
        {
            var secondsOfDelay = BrokeredMessageReceiverRetry.ExponentialDelay(msg.SystemProperties.DeliveryCount);
            await Task.Delay(secondsOfDelay * BrokeredMessageReceiverRetry.MillisecondsInASecond);
            await AbandonAsync(msg).ConfigureAwait(false);
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
    }
}
