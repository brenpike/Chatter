using Chatter.MessageBrokers.AzureServiceBus.DependencyInjection;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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
        // The AMQP-level signal Azure Service Bus returns when a cross-entity-transaction client touches a
        // second top-level entity. The SDK surfaces this as a non-transient ServiceBusException (or an
        // InvalidOperationException from the client-side enlistment guard); the message text is the stable
        // discriminator across SDK versions and exception shapes.
        private const string CrossEntityTransactionRejectionMarker = "multiple top-level entities";

        readonly object _syncLock;
        private readonly ILogger<ServiceBusReceiver> _logger;
        private readonly InboundBrokeredMessageFactory _inboundFactory;
        private readonly Func<ReceiverOptions, ServiceBusReceiveMode, IServiceBusMessageReceiver> _receiverFactory;
        private readonly ServiceBusOptions _serviceBusOptions;
        private readonly ServiceBusReceiverRegistry _receiverRegistry;
        private ServiceBusReceiveMode _receiveMode;
        // INVARIANT: the receiver and sender MUST share ONE ServiceBusClient per namespace so the send and
        // the receiver's settle enlist in one cross-entity transaction (EnableCrossEntityTransactions). The
        // client is the DI-registered singleton injected here, NOT one this receiver constructs.
        private readonly ServiceBusClient _client;
        IServiceBusMessageReceiver _innerReceiver;
        private bool _disposedValue;
        private ReceiverOptions _options;

        // receiverRegistry is optional so existing callers/tests that construct via the five-argument shape
        // keep compiling; DI always supplies the registered singleton on the production path, and the
        // production session-vs-non-session branch null-guards it. When null, only the non-session adapter
        // is ever selected (the prior behavior).
        public ServiceBusReceiver(ServiceBusClient client,
                                  ServiceBusOptions serviceBusOptions,
                                  MessageBrokerOptions messageBrokerOptions,
                                  ILogger<ServiceBusReceiver> logger,
                                  IBodyConverterFactory bodyConverterFactory,
                                  ServiceBusReceiverRegistry receiverRegistry = null)
            : this(client,
                   serviceBusOptions,
                   messageBrokerOptions,
                   logger,
                   new InboundBrokeredMessageFactory(
                       bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory)),
                       logger ?? throw new ArgumentNullException(nameof(logger))),
                   receiverFactory: null,
                   receiverRegistry: receiverRegistry)
        { }

        // Internal seam ctor: an IServiceBusMessageReceiver factory (path + receive-mode -> port) can be
        // injected to drive receive/ack behavior with an in-memory double in tests. When null, the
        // production CreateProductionReceiver source (off the shared client) is used; that production path
        // is the only caller that reads receiverRegistry, so a test supplying its own receiverFactory may
        // leave receiverRegistry null.
        internal ServiceBusReceiver(ServiceBusClient client,
                                    ServiceBusOptions serviceBusOptions,
                                    MessageBrokerOptions messageBrokerOptions,
                                    ILogger<ServiceBusReceiver> logger,
                                    InboundBrokeredMessageFactory inboundFactory,
                                    Func<ReceiverOptions, ServiceBusReceiveMode, IServiceBusMessageReceiver> receiverFactory,
                                    ServiceBusReceiverRegistry receiverRegistry = null)
        {
            if (serviceBusOptions is null)
            {
                throw new ArgumentNullException(nameof(serviceBusOptions));
            }

            _syncLock = new object();
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _serviceBusOptions = serviceBusOptions;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _inboundFactory = inboundFactory ?? throw new ArgumentNullException(nameof(inboundFactory));
            _receiveMode = messageBrokerOptions?.TransactionMode == TransactionMode.None ? ServiceBusReceiveMode.ReceiveAndDelete : ServiceBusReceiveMode.PeekLock;
            _receiverRegistry = receiverRegistry;
            _receiverFactory = receiverFactory ?? CreateProductionReceiver;
        }

        // Selects the production IServiceBusMessageReceiver for the receiver's entity: the session adapter
        // when the registry marks the entity session-mode, otherwise the existing non-session adapter. The
        // top-level entity is inferred from SendingPath/MessageReceiverPath exactly as registration does
        // (queue receiver -> receiver path; topic subscription -> sending path), so the case-insensitive
        // registry lookup keys on the same value the receiver was registered under.
        private IServiceBusMessageReceiver CreateProductionReceiver(ReceiverOptions options, ServiceBusReceiveMode receiveMode)
        {
            var topLevelEntity = InferTopLevelEntity(options.SendingPath, options.MessageReceiverPath);

            if (_receiverRegistry != null && _receiverRegistry.RequiresSession(topLevelEntity))
            {
                return new AzureSdkSessionMessageReceiverAdapter(_client,
                                                                 options.MessageReceiverPath,
                                                                 receiveMode,
                                                                 _serviceBusOptions.PrefetchCount,
                                                                 _serviceBusOptions.SessionIdleTimeout,
                                                                 _serviceBusOptions.MaxSessionLockRenewalDuration,
                                                                 _logger);
            }

            return new AzureSdkMessageReceiverAdapter(_client,
                                                      options.MessageReceiverPath,
                                                      receiveMode,
                                                      _serviceBusOptions.PrefetchCount,
                                                      _logger);
        }

        // Mirrors the registration-time top-level-entity inference: a queue receiver's sending path is empty
        // or equals its receiver path (the queue IS the top-level entity); a topic subscription's sending
        // path is the distinct topic (the TOPIC is the top-level entity).
        private static string InferTopLevelEntity(string sendingPath, string messageReceiverPath)
        {
            if (string.IsNullOrWhiteSpace(sendingPath) || string.Equals(sendingPath, messageReceiverPath, StringComparison.Ordinal))
            {
                return messageReceiverPath;
            }

            return sendingPath;
        }

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
                message = await this.InnerReceiver.ReceiveAsync(cancellationToken);
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
            catch (Exception e) when (IsCrossEntityTransactionRejection(e))
            {
                // Defense-in-depth behind the DI-time startup guard: when cross-entity transactions are on,
                // Azure Service Bus pins the shared client to the first top-level entity it touches and
                // rejects a second receiver on a different top-level entity ("cannot span multiple top-level
                // entities"). This is fatal and non-recoverable, so it is rethrown as CriticalReceiverException
                // to stop the core receive loop loudly instead of being retried as a transient failure.
                _logger.LogCritical(e, "Azure Service Bus rejected the receiver because cross-entity transactions cannot span multiple top-level entities");
                throw new CriticalReceiverException(e);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Teardown path: the core receive loop cancelled its token to stop the pump, and the inner
                // ASB receive observed it. Treat this as "nothing received" so the loop releases its slot and
                // exits via its IsCancellationRequested guard — no error log, no nack/settle. TaskCanceledException
                // is a subclass of OperationCanceledException, so it is covered here too.
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

            // Session path only: include the held SDK session receiver so the session-state extension and
            // session settlement can resolve it during handling. Non-session receivers leave the container
            // unchanged (the held receiver is null when no session adapter is in use).
            if (this.InnerReceiver is AzureSdkSessionMessageReceiverAdapter sessionAdapter
                && sessionAdapter.HeldSessionReceiver != null)
            {
                transactionContext.Container.Include(sessionAdapter.HeldSessionReceiver);
            }

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

            await this.InnerReceiver.AbandonAsync(msg, new Dictionary<string, object>(msg.ApplicationProperties));
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

        // Classifies a receive-path exception as the fatal cross-entity-transaction rejection. Matches both
        // the non-transient ServiceBusException and the InvalidOperationException shapes the SDK may raise, on
        // the stable "multiple top-level entities" message marker. A transient ServiceBusException is NOT
        // matched here — it is handled by the dedicated transient branch above.
        private static bool IsCrossEntityTransactionRejection(Exception exception)
        {
            if (exception is ServiceBusException sbe && sbe.IsTransient)
            {
                return false;
            }

            return (exception is ServiceBusException || exception is InvalidOperationException)
                && exception.Message != null
                && exception.Message.IndexOf(CrossEntityTransactionRejectionMarker, StringComparison.OrdinalIgnoreCase) >= 0;
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
