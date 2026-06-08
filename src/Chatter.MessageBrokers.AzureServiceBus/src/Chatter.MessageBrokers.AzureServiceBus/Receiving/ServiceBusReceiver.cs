using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    internal class ServiceBusReceiver : IMessagingInfrastructureReceiver
    {
        // INVARIANT: the Azure Service Bus SDK rejects a deadletter error description longer than 4096
        // UTF-16 chars with ArgumentOutOfRangeException (Parameter 'deadLetterErrorDescription'), so the
        // description is capped at this length (matching the SDK's char-based validation) before dispatch.
        private const int MaxDeadLetterErrorDescriptionLength = 4096;
        private const string DeadLetterErrorDescriptionTruncationMarker = "…[truncated]";

        readonly object _syncLock;
        private readonly ILogger<ServiceBusReceiver> _logger;
        private readonly InboundBrokeredMessageFactory _inboundFactory;
        private readonly Func<ReceiverOptions, ServiceBusReceiveMode, IServiceBusMessageReceiver> _receiverFactory;
        private readonly ServiceBusOptions _serviceBusOptions;
        private ServiceBusReceiveMode _receiveMode;
        // TODO(STEP-006): the shared ServiceBusClient will be injected from DI rather than constructed here
        // from ServiceBusOptions. Until then the receiver lazily builds a client from the options so the
        // production adapter has a client to create its SDK receiver from.
        private ServiceBusClient _client;
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
                                    Func<ReceiverOptions, ServiceBusReceiveMode, IServiceBusMessageReceiver> receiverFactory)
        {
            if (serviceBusOptions is null)
            {
                throw new ArgumentNullException(nameof(serviceBusOptions));
            }

            _syncLock = new object();
            _serviceBusOptions = serviceBusOptions;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _inboundFactory = inboundFactory ?? throw new ArgumentNullException(nameof(inboundFactory));
            _receiveMode = messageBrokerOptions?.TransactionMode == TransactionMode.None ? ServiceBusReceiveMode.ReceiveAndDelete : ServiceBusReceiveMode.PeekLock;
            _receiverFactory = receiverFactory ?? CreateProductionReceiver;
        }

        // TODO(STEP-006): replace this options-derived client with the shared ServiceBusClient injected
        // from DI. A null TokenCredential means "authenticate with the connection string's SAS".
        private ServiceBusClient Client
        {
            get
            {
                if (_client == null)
                {
                    lock (_syncLock)
                    {
                        if (_client == null)
                        {
                            var clientOptions = new ServiceBusClientOptions();
                            if (_serviceBusOptions.RetryOptions != null)
                            {
                                clientOptions.RetryOptions = _serviceBusOptions.RetryOptions;
                            }

                            _client = _serviceBusOptions.TokenCredential is null
                                ? new ServiceBusClient(_serviceBusOptions.ConnectionString, clientOptions)
                                : new ServiceBusClient(_serviceBusOptions.ConnectionString, _serviceBusOptions.TokenCredential, clientOptions);
                        }
                    }
                }

                return _client;
            }
        }

        private IServiceBusMessageReceiver CreateProductionReceiver(ReceiverOptions options, ServiceBusReceiveMode receiveMode)
            => new AzureSdkMessageReceiverAdapter(Client,
                                                  options.MessageReceiverPath,
                                                  receiveMode,
                                                  _serviceBusOptions.PrefetchCount,
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
                _receiveMode = _options.TransactionMode == TransactionMode.None ? ServiceBusReceiveMode.ReceiveAndDelete : ServiceBusReceiveMode.PeekLock;
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
            ServiceBusReceivedMessage message;

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
                // INVARIANT: the received message (NOT a connection) is carried in the container so
                // STEP-004's send path can enlist it. Cross-entity transactions on the shared client
                // handle atomicity (wired in STEP-004/006); the old ServiceBusConnection mechanism is gone.
                transactionContext.Container.Include(message);
            }

            return messageContext;
        }

        public async Task<bool> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                return false;
            }

            if (!context.Container.TryGet<ServiceBusReceivedMessage>(out var msg))
            {
                _logger.LogWarning($"Unable to acknowledge message. No {nameof(ServiceBusReceivedMessage)} contained in {nameof(context)}.");
                return false;
            }

            await this.InnerReceiver.CompleteAsync(msg);
            _logger.LogTrace($"Message '{msg.MessageId}' completed");
            return true;
        }

        public async Task<bool> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                return false;
            }

            if (!context.Container.TryGet<ServiceBusReceivedMessage>(out var msg))
            {
                _logger.LogWarning($"Unable to negative acknowledge message. No {nameof(ServiceBusReceivedMessage)} contained in {nameof(context)}.");
                return false;
            }

            await this.InnerReceiver.AbandonAsync(msg, msg.ApplicationProperties);
            _logger.LogTrace($"Message '{msg.MessageId}' sucessfully abandoned");
            return true;
        }

        public async Task<bool> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                return false;
            }

            if (!context.Container.TryGet<ServiceBusReceivedMessage>(out var msg))
            {
                _logger.LogWarning($"Unable to deadletter message. No {nameof(ServiceBusReceivedMessage)} contained in {nameof(context)}.");
                return false;
            }

            await this.InnerReceiver.DeadLetterAsync(msg, deadLetterReason, CapDeadLetterErrorDescription(deadLetterErrorDescription));
            _logger.LogTrace($"Message '{msg.MessageId}' sucessfully deadlettered");
            return true;
        }

        // Caps the deadletter error description at the SDK's MaxDeadLetterErrorDescriptionLength UTF-16
        // chars. A null/empty or already-fitting description is returned unchanged; an over-limit
        // description preserves its diagnostic head and appends a truncation marker, reserving the
        // marker's length inside the budget so the result never exceeds the limit.
        private static string CapDeadLetterErrorDescription(string deadLetterErrorDescription)
        {
            if (string.IsNullOrEmpty(deadLetterErrorDescription)
                || deadLetterErrorDescription.Length <= MaxDeadLetterErrorDescriptionLength)
            {
                return deadLetterErrorDescription;
            }

            var headLength = MaxDeadLetterErrorDescriptionLength - DeadLetterErrorDescriptionTruncationMarker.Length;
            return deadLetterErrorDescription.Substring(0, headLength) + DeadLetterErrorDescriptionTruncationMarker;
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
