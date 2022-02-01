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
                await _innerReceiver.CloseAsync().ConfigureAwait(false);
            }
        }

        public async Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            Message message;

            try
            {
                message = await this.InnerReceiver.ReceiveAsync();
            }
            catch (ServiceBusException sbe) when (sbe.IsTransient)
            {
                _logger.LogError("Failure to receive message from Azure Service Bus due to transient error");
                return null;
            }
            catch (Exception)
            {
                _logger.LogError("Failure to receive message from Azure Serivce Bus");
                throw;
            }

            if (message == null)
            {
                return null;
            }

            try
            {
                MessageBrokerContext messageContext = null;

                message.AddUserProperty(MessageContext.TimeToLive, message.TimeToLive);
                message.AddUserProperty(MessageContext.ExpiryTimeUtc, message.ExpiresAtUtc);
                message.AddUserProperty(MessageContext.InfrastructureType, ASBMessageContext.InfrastructureType);

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

        public Task AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (!context.Container.TryGet<Message>(out var msg))
            {
                _logger.LogWarning($"{nameof(transactionContext.TransactionMode)} was set but no {nameof(Message)} was found in {nameof(context)}. Unable to acknowledge message.");
                return Task.CompletedTask;
            }

            return this.InnerReceiver.CompleteAsync(msg.SystemProperties.LockToken);
        }

        public Task NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (!context.Container.TryGet<Message>(out var msg))
            {
                _logger.LogWarning($"{nameof(transactionContext.TransactionMode)} was set but no {nameof(Message)} was found in {nameof(context)}. Unable to negative acknowledge message.");
                return Task.CompletedTask;
            }

            return this.InnerReceiver.AbandonAsync(msg.SystemProperties.LockToken, msg.UserProperties);
        }

        public Task DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            if (!context.Container.TryGet<Message>(out var msg))
            {
                _logger.LogWarning($"{nameof(transactionContext.TransactionMode)} was set but no {nameof(Message)} was found in {nameof(context)}. Unable to dead letter message.");
                return Task.CompletedTask;
            }

            return this.InnerReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, deadLetterReason, deadLetterErrorDescription);
        }

        public Task<int> CurrentMessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken)
        {
            if (!context.Container.TryGet<Message>(out var msg))
            {
                _logger.LogWarning($"No {nameof(Message)} was found in {nameof(context)}. Unable to fetch delivery count message.");
                return Task.FromResult(999);
            }

            return Task.FromResult(msg.SystemProperties.DeliveryCount);
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
