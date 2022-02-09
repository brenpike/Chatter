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
        private readonly RetryPolicy _retryPolcy;
        private readonly int _prefetchCount;
        private ReceiveMode _receiveMode;
        MessageReceiver _innerReceiver;
        private bool _disposedValue;
        private ReceiverOptions _options;

        public ServiceBusReceiver(ServiceBusOptions serviceBusOptions,
                                  MessageBrokerOptions messageBrokerOptions,
                                  ILogger<ServiceBusReceiver> logger,
                                  IBodyConverterFactory bodyConverterFactory)
        {
            if (serviceBusOptions is null)
            {
                throw new ArgumentNullException(nameof(serviceBusOptions));
            }

            _syncLock = new object();
            ServiceBusConnectionBuilder = new ServiceBusConnectionStringBuilder(serviceBusOptions.ConnectionString);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _tokenProvider = serviceBusOptions.TokenProvider;
            _retryPolcy = serviceBusOptions.Policy;
            _prefetchCount = serviceBusOptions.PrefetchCount;
            _receiveMode = messageBrokerOptions?.TransactionMode == TransactionMode.None ? ReceiveMode.ReceiveAndDelete : ReceiveMode.PeekLock;
        }

        /// <summary>
        /// Connection object to the service bus namespace.
        /// </summary>
        public ServiceBusConnectionStringBuilder ServiceBusConnectionBuilder { get; }

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
                                                                     _options.MessageReceiverPath,
                                                                     _receiveMode,
                                                                     _retryPolcy,
                                                                     _prefetchCount);
                            }
                            else
                            {
                                _innerReceiver = new MessageReceiver(this.ServiceBusConnectionBuilder.Endpoint,
                                                                     _options.MessageReceiverPath,
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

        public Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            _options = options;
            if (options.TransactionMode != null)
            {
                _receiveMode = _options.TransactionMode == TransactionMode.None ? ReceiveMode.ReceiveAndDelete : ReceiveMode.PeekLock;
            }

            return Task.CompletedTask;
        }

        public async Task StopReceiver()
        {
            if (_innerReceiver != null)
            {
                await _innerReceiver.CloseAsync();
            }
        }

        public async Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            Message message;

            try
            {
                message = await this.InnerReceiver.ReceiveAsync();
                //todo: add catch blocks for non-transient exceptions which can never be recovered (critical error), i.e., invalid connection string, etc.
                //throw new CriticalReceiverException(ae);
            }
            catch (ServiceBusException sbe) when (sbe.IsTransient)
            {
                _logger.LogWarning(sbe, "Failure to receive message from Azure Service Bus due to transient error");
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failure to receive message from Azure Serivce Bus");
                throw;
            }

            if (message is null)
            {
                return null;
            }

            try
            {
                MessageBrokerContext messageContext = null;

                message.AddUserProperty(MessageContext.TimeToLive, message.TimeToLive);
                message.AddUserProperty(MessageContext.ExpiryTimeUtc, message.ExpiresAtUtc);
                message.AddUserProperty(MessageContext.InfrastructureType, ASBMessageContext.InfrastructureType);
                message.AddUserProperty(MessageContext.ReceiveAttempts, message.SystemProperties.DeliveryCount);

                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(message.ContentType);

                messageContext = new MessageBrokerContext(message.MessageId, message.Body, message.UserProperties, _options.MessageReceiverPath, cancellationToken, bodyConverter);
                messageContext.Container.Include(message);

                transactionContext.Container.Include(this.InnerReceiver);

                if (_options.TransactionMode == TransactionMode.FullAtomicityViaInfrastructure)
                {
                    transactionContext.Container.Include(this.InnerReceiver.ServiceBusConnection);
                }

                return messageContext;
            }
            catch (Exception e)
            {
                throw new PoisonedMessageException($"Unable to build {typeof(MessageBrokerContext).Name} due to poisoned message", e);
            }
        }

        public async Task<bool> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (_receiveMode != ReceiveMode.PeekLock)
            {
                return false;
            }

            if (!context.Container.TryGet<Message>(out var msg))
            {
                _logger.LogWarning($"Unable to acknowledge message. No {nameof(Message)} contained in {nameof(context)}.");
                return false;
            }

            await this.InnerReceiver.CompleteAsync(msg.SystemProperties.LockToken);
            return true;
        }

        public async Task<bool> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (_receiveMode != ReceiveMode.PeekLock)
            {
                return false;
            }

            if (!context.Container.TryGet<Message>(out var msg))
            {
                _logger.LogWarning($"Unable to negative acknowledge message.  No {nameof(Message)} contained in {nameof(context)}.");
                return false;
            }

            await this.InnerReceiver.AbandonAsync(msg.SystemProperties.LockToken, msg.UserProperties);
            return true;
        }

        public async Task<bool> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            if (_receiveMode != ReceiveMode.PeekLock)
            {
                return false;
            }

            if (!context.Container.TryGet<Message>(out var msg))
            {
                _logger.LogWarning($"Unable to deadletter message.  No {nameof(Message)} contained in {nameof(context)}.");
                return false;
            }

            await this.InnerReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, deadLetterReason, deadLetterErrorDescription);
            return true;
        }

        public TransactionScope CreateLocalTransaction(TransactionContext context)
        {
            if (context.TransactionMode == TransactionMode.None || context.TransactionMode == TransactionMode.ReceiveOnly)
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
                    _innerReceiver?.CloseAsync();
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
