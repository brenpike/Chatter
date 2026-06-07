using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    internal class ServiceBusReceiver : IMessagingInfrastructureReceiver
    {
        readonly object _syncLock;
        private readonly ILogger<ServiceBusReceiver> _logger;
        private readonly InboundBrokeredMessageFactory _inboundFactory;
        private readonly Func<ReceiverOptions, ReceiveMode, IServiceBusMessageReceiver> _receiverFactory;
        private readonly ITokenProvider _tokenProvider;
        private readonly RetryPolicy _retryPolcy;
        private readonly int _prefetchCount;
        private ReceiveMode _receiveMode;
        IServiceBusMessageReceiver _innerReceiver;
        private bool _disposedValue;
        private ReceiverOptions _options;

        public ServiceBusReceiver(ServiceBusOptions serviceBusOptions,
                                  MessageBrokerOptions messageBrokerOptions,
                                  ILogger<ServiceBusReceiver> logger,
                                  IBodyConverterFactory bodyConverterFactory)
            : this(serviceBusOptions,
                   messageBrokerOptions,
                   logger,
                   new InboundBrokeredMessageFactory(
                       bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory)),
                       logger ?? throw new ArgumentNullException(nameof(logger))),
                   receiverFactory: null)
        { }

        // Internal seam ctor: an IServiceBusMessageReceiver factory (path + receive-mode -> port) can be
        // injected to drive receive/ack behavior with an in-memory double in tests. When null, the
        // production AzureSdkMessageReceiverAdapter source is used.
        internal ServiceBusReceiver(ServiceBusOptions serviceBusOptions,
                                    MessageBrokerOptions messageBrokerOptions,
                                    ILogger<ServiceBusReceiver> logger,
                                    InboundBrokeredMessageFactory inboundFactory,
                                    Func<ReceiverOptions, ReceiveMode, IServiceBusMessageReceiver> receiverFactory)
        {
            if (serviceBusOptions is null)
            {
                throw new ArgumentNullException(nameof(serviceBusOptions));
            }

            _syncLock = new object();
            ServiceBusConnectionBuilder = new ServiceBusConnectionStringBuilder(serviceBusOptions.ConnectionString);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _inboundFactory = inboundFactory ?? throw new ArgumentNullException(nameof(inboundFactory));
            _tokenProvider = serviceBusOptions.TokenProvider;
            _retryPolcy = serviceBusOptions.Policy;
            _prefetchCount = serviceBusOptions.PrefetchCount;
            _receiveMode = messageBrokerOptions?.TransactionMode == TransactionMode.None ? ReceiveMode.ReceiveAndDelete : ReceiveMode.PeekLock;
            _receiverFactory = receiverFactory ?? CreateProductionReceiver;
        }

        /// <summary>
        /// Connection object to the service bus namespace.
        /// </summary>
        public ServiceBusConnectionStringBuilder ServiceBusConnectionBuilder { get; }

        private IServiceBusMessageReceiver CreateProductionReceiver(ReceiverOptions options, ReceiveMode receiveMode)
            => new AzureSdkMessageReceiverAdapter(ServiceBusConnectionBuilder,
                                                  options.MessageReceiverPath,
                                                  receiveMode,
                                                  _retryPolcy,
                                                  _prefetchCount,
                                                  _tokenProvider,
                                                  _logger);

        internal IServiceBusMessageReceiver InnerReceiver
        {
            get
            {
                if (_innerReceiver == null)
                {
                    lock (_syncLock)
                    {
                        if (_innerReceiver == null)
                        {
                            _innerReceiver = _receiverFactory(_options, _receiveMode);
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
            }
            catch (ServiceBusException sbe) when (sbe.IsTransient)
            {
                _logger.LogWarning(sbe, "Failure to receive message from Azure Service Bus due to transient error");
                throw;
            }
            catch (ObjectDisposedException e) when (!cancellationToken.IsCancellationRequested && _innerReceiver.IsClosedOrClosing)
            {
                lock (_syncLock)
                {
                    _innerReceiver = null;
                }

                _logger.LogWarning(e, "Service Bus receiver connection was closed.");

                return null;
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

            var messageContext = _inboundFactory.CreateContext(message, _options.MessageReceiverPath, cancellationToken);

            transactionContext.Container.Include(this.InnerReceiver);

            if (_options.TransactionMode == TransactionMode.FullAtomicityViaInfrastructure)
            {
                transactionContext.Container.Include(this.InnerReceiver.ServiceBusConnection);
            }

            return messageContext;
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
            _logger.LogTrace($"Message '{msg.MessageId}' completed");
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
                _logger.LogWarning($"Unable to negative acknowledge message. No {nameof(Message)} contained in {nameof(context)}.");
                return false;
            }

            await this.InnerReceiver.AbandonAsync(msg.SystemProperties.LockToken, msg.UserProperties);
            _logger.LogTrace($"Message '{msg.MessageId}' sucessfully abandoned");
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
                _logger.LogWarning($"Unable to deadletter message. No {nameof(Message)} contained in {nameof(context)}.");
                return false;
            }

            await this.InnerReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, deadLetterReason, deadLetterErrorDescription);
            _logger.LogTrace($"Message '{msg.MessageId}' sucessfully deadlettered");
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
