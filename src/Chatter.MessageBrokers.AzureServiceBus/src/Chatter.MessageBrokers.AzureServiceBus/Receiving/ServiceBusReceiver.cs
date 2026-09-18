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
    internal class ServiceBusReceiver : IMessagingInfrastructureReceiver, IDeliveryReleaseSignal
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
        // Why a non-PeekLock settlement is owed nothing: ReceiveAndDelete removes the delivery as it is
        // received, so there is no lock left to release and reporting a missing settlement would report a
        // failure that never occurred.
        private const string NothingToSettleReason = "the delivery was received in ReceiveAndDelete mode, so Azure Service Bus removed it on receipt and no settlement is owed";

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

        // Selects the production IServiceBusMessageReceiver for THIS receiver: the session adapter when the
        // registry marks this specific receiver session-mode, otherwise the existing non-session adapter. The
        // session lookup is PER-RECEIVER, keyed on this receiver's own (MessageReceiverPath, SendingPath) pair
        // — the SAME pair it was registered under — so a session subscription and a normal subscription on the
        // same topic resolve to different adapters instead of colliding on the shared top-level entity.
        private IServiceBusMessageReceiver CreateProductionReceiver(ReceiverOptions options, ServiceBusReceiveMode receiveMode)
        {
            if (_receiverRegistry != null && _receiverRegistry.RequiresSession(options.MessageReceiverPath, options.SendingPath))
            {
                var sessionEntityPath = ServiceBusSessionEntityPath.Create(options.SendingPath, options.MessageReceiverPath);

                // In SESSION mode MaxConcurrentCalls means CONCURRENT SESSIONS (ADR-0014): above one, the
                // multiplexer holds that many sessions by owning one single-session child each, and each child
                // still serves its own session's messages one at a time. At one, the bare adapter is used
                // directly with no multiplexer in the path, so today's object graph is unchanged.
                if (options.MaxConcurrentCalls > 1)
                {
                    return new SessionReceiverMultiplexer(options.MaxConcurrentCalls,
                                                          () => CreateSessionChildReceiver(sessionEntityPath, receiveMode),
                                                          options.MessageReceiverPath,
                                                          _logger);
                }

                return CreateSessionChildReceiver(sessionEntityPath, receiveMode);
            }

            return new AzureSdkMessageReceiverAdapter(_client,
                                                      options.MessageReceiverPath,
                                                      receiveMode,
                                                      _serviceBusOptions.PrefetchCount,
                                                      _serviceBusOptions.MaxMessageLockRenewalDuration,
                                                      _logger);
        }

        // The single-session adapter, built identically whether it is used bare (N = 1) or as one of the
        // multiplexer's children (N > 1).
        private AzureSdkSessionMessageReceiverAdapter CreateSessionChildReceiver(ServiceBusSessionEntityPath sessionEntityPath, ServiceBusReceiveMode receiveMode)
            => new AzureSdkSessionMessageReceiverAdapter(_client,
                                                         sessionEntityPath,
                                                         receiveMode,
                                                         _serviceBusOptions.PrefetchCount,
                                                         _serviceBusOptions.SessionIdleTimeout,
                                                         _serviceBusOptions.MaxSessionLockRenewalDuration,
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
                message = await this.InnerReceiver.ReceiveAsync(cancellationToken);
            }
            catch (ServiceBusException sbe) when (sbe.IsTransient)
            {
                _logger.LogWarning(sbe, "Failure to receive message from Azure Service Bus due to transient error");
                throw;
            }
            catch (ObjectDisposedException e) when (!cancellationToken.IsCancellationRequested && _innerReceiver.IsClosedOrClosing)
            {
                // The discarded receiver is captured and cleared in ONE lock so two threads landing here cannot
                // both close it, and the next receive lazily rebuilds it.
                IServiceBusMessageReceiver discardedReceiver;
                lock (_syncLock)
                {
                    discardedReceiver = _innerReceiver;
                    _innerReceiver = null;
                }

                _logger.LogWarning(e, "Service Bus receiver connection was closed.");
                CloseWithoutAwaiting(discardedReceiver, "Failure closing the discarded Azure Service Bus receiver");

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

            // Session path only: include the session receiver that delivered THIS message so the session-state
            // extension and session settlement can resolve it during handling. The port answers per message, so
            // one check covers both shapes — a receiver holding one session answers with that session, a
            // multiplexer answers with the session belonging to this message. Non-session receivers leave the
            // container unchanged, as does a session receiver that holds no session for this message.
            if (this.InnerReceiver is IServiceBusSessionMessageReceiver sessionReceiver
                && sessionReceiver.SessionReceiverFor(message) is { } heldSessionReceiver)
            {
                transactionContext.Container.Include(heldSessionReceiver);
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

        public async Task<SettlementResult> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                return SettlementResult.NotRequired(NothingToSettleReason);
            }

            if (!context.Container.TryGet<ServiceBusReceivedMessage>(out var msg))
            {
                return SettlementResult.Failed(DescribeUnsettledDelivery("complete"));
            }

            var completion = await this.InnerReceiver.CompleteAsync(msg);
            if (completion != ServiceBusSettlementOutcome.Settled)
            {
                return ReportUnsettledDelivery(completion, "complete");
            }

            _logger.LogTrace($"Message '{msg.MessageId}' completed");
            return SettlementResult.Settled();
        }

        public async Task<SettlementResult> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                return SettlementResult.NotRequired(NothingToSettleReason);
            }

            if (!context.Container.TryGet<ServiceBusReceivedMessage>(out var msg))
            {
                return SettlementResult.Failed(DescribeUnsettledDelivery("abandon"));
            }

            var abandonment = await this.InnerReceiver.AbandonAsync(msg, new Dictionary<string, object>(msg.ApplicationProperties));
            if (abandonment != ServiceBusSettlementOutcome.Settled)
            {
                return ReportUnsettledDelivery(abandonment, "abandon");
            }

            _logger.LogTrace($"Message '{msg.MessageId}' sucessfully abandoned");
            return SettlementResult.Settled();
        }

        public async Task<SettlementResult> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                return SettlementResult.NotRequired(NothingToSettleReason);
            }

            if (!context.Container.TryGet<ServiceBusReceivedMessage>(out var msg))
            {
                return SettlementResult.Failed(DescribeUnsettledDelivery("deadletter"));
            }

            var deadLettering = await this.InnerReceiver.DeadLetterAsync(msg, deadLetterReason, CapDeadLetterErrorDescription(deadLetterErrorDescription));
            if (deadLettering != ServiceBusSettlementOutcome.Settled)
            {
                return ReportUnsettledDelivery(deadLettering, "deadletter");
            }

            _logger.LogTrace($"Message '{msg.MessageId}' sucessfully deadlettered");
            return SettlementResult.Settled();
        }

        /// <summary>
        /// Tells the inner receiver that the worker is finished with this delivery. Every inner receiver has
        /// something to end with it: a session receiver frees the session slot the delivery occupied, a non-session
        /// receiver ends the delivery's message-lock renewal.
        /// </summary>
        /// <remarks>
        /// INVARIANT: this reads the inner receiver FIELD, never the lazily-constructing <see cref="InnerReceiver"/>
        /// property. The signal fires on teardown paths too, and the property would build a brand-new receiver —
        /// accepting sessions of its own — only to tell it about a delivery it never handled.
        /// This MUST NOT throw (<see cref="IDeliveryReleaseSignal"/>): a null field, a null context and a context
        /// carrying no received message are each answered by doing nothing.
        /// </remarks>
        public void DeliveryReleased(MessageBrokerContext context)
        {
            var innerReceiver = _innerReceiver;
            if (innerReceiver != null
                && context != null
                && context.Container.TryGet<ServiceBusReceivedMessage>(out var msg))
            {
                innerReceiver.DeliveryReleased(msg);
            }
        }

        // Closes a receiver WITHOUT awaiting it, on the two paths that cannot await one: the receive path
        // discarding a receiver after an ObjectDisposedException, and the synchronous Dispose. A discarded session
        // receiver that is never closed orphans the sessions it holds, their lock-renewal loops and their armed
        // receives, which keep locking sessions no worker will ever process while the rebuilt receiver competes
        // with its own abandoned predecessor for them; on the Dispose path there is no rebuilt receiver to compete
        // with, and the sessions stay orphaned until their locks expire. Either way the close attempt is OBSERVED —
        // anything other than a successful close is logged rather than left unobserved.
        //
        // INVARIANT: this method never throws, and it observes EVERY outcome of the close it starts. Both
        // properties are held by PARTITIONING the close rather than by enumerating the outcomes worth
        // reporting — an enumeration can only ever name the outcomes known when it was written, and an
        // outcome it did not name is a teardown failure nobody hears about. The close is covered by exactly
        // two regions: everything up to and including attaching the continuation, and the continuation
        // itself. Each region REPORTS BY DEFAULT and exempts only the single success state, so an outcome
        // that did not exist when this was written is already in the reported set.
        private void CloseWithoutAwaiting(IServiceBusMessageReceiver receiverToClose, string closeFailureMessage)
        {
            if (receiverToClose == null)
            {
                return;
            }

            // Region one: starting the close. A close that throws before returning a task, and a close that
            // returns no task to observe at all, are both failures of the same thing — producing something the
            // continuation can report through — so the missing task is turned into a throw and handled by the
            // region instead of by a check that names it.
            try
            {
                var closeAttempt = receiverToClose.CloseAsync()
                    ?? throw new InvalidOperationException($"{nameof(IServiceBusMessageReceiver.CloseAsync)} returned no task, so the close outcome cannot be observed");

                // Region two: the close's own outcome. ExecuteSynchronously ONLY — no outcome filter — so the
                // continuation DECIDES for every terminal state what to report. ExecuteSynchronously is a HINT:
                // the continuation runs inline when the TPL honors it, and is queued to TaskScheduler.Default
                // when it declines. This helper does not wait for it either way, so a queued report can be lost
                // at process exit on the Dispose path — see the accepted residual in ADR-0022.
                _ = closeAttempt.ContinueWith(
                    completedCloseAttempt => ReportUnsuccessfulClose(completedCloseAttempt, closeFailureMessage),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (Exception closeFailure)
            {
                // The caller's teardown must still finish: Dispose has to null the inner receiver and latch
                // _disposedValue, which a propagating throw would skip, and the receive path is discarding this
                // receiver regardless.
                ReportCloseFailure(closeFailure, closeFailureMessage);
            }
        }

        // The continuation body: reports every terminal state of a close EXCEPT success. A cancelled close
        // carries no exception — it was abandoned partway, leaving the same orphaned sessions and renewals a
        // faulted close leaves — so the terminal status travels in the message and the exception may be null.
        private void ReportUnsuccessfulClose(Task completedCloseAttempt, string closeFailureMessage)
        {
            if (completedCloseAttempt.Status == TaskStatus.RanToCompletion)
            {
                return;
            }

            ReportCloseFailure(completedCloseAttempt.Exception, $"{closeFailureMessage}. The close ended as {completedCloseAttempt.Status}");
        }

        // The single reporting channel both regions use, non-throwing by construction.
        // INVARIANT: this swallows its own failure DELIBERATELY, and it is where the observation regress
        // terminates. The logger IS the channel a close failure is reported through, so a failure OF that
        // channel has nowhere left to be reported, and an observer that can itself fail would need its own
        // observer without end. Swallowing here is what makes the never-throws invariant above hold for the
        // reporting step itself.
        private void ReportCloseFailure(Exception closeFailure, string closeFailureMessage)
        {
            try
            {
                _logger.LogWarning(closeFailure, closeFailureMessage);
            }
            catch
            {
            }
        }

        // Maps a settle call that reported something OTHER than a settlement onto the outcome the core records.
        // INVARIANT: never Settled — the infrastructure just said it did not settle the delivery, so answering
        // Settled here would record an acknowledgement the broker never received.
        private static SettlementResult ReportUnsettledDelivery(ServiceBusSettlementOutcome outcome, string settlementAction)
            => outcome switch
            {
                ServiceBusSettlementOutcome.NotOwed => SettlementResult.NotRequired(NothingToSettleReason),
                _ => SettlementResult.Failed(DescribeUnreachableDelivery(settlementAction)),
            };

        // Why a PeekLock settlement could not happen: the infrastructure can no longer reach the delivery the
        // settlement targets — a session receiver whose held session was released before settlement ran no longer
        // owns the lock, so the broker still holds the delivery and will redeliver it. Like the absent-delivery
        // case this is DETERMINISTIC, so it is reported as a Failed outcome instead of being thrown for Recovery
        // to retry.
        private static string DescribeUnreachableDelivery(string settlementAction)
            => $"Unable to {settlementAction} the Azure Service Bus message. The delivery could no longer be reached by the receiver that delivered it, so the PeekLock delivery was not settled.";

        // Why a PeekLock settlement could not happen: the delivery the settlement targets is absent from the
        // message broker context, so there is a lock to release and no message to release it with. This is
        // DETERMINISTIC — retrying the same context would find the same absence — which is why it is reported
        // as a Failed outcome instead of being thrown for Recovery to retry.
        private static string DescribeUnsettledDelivery(string settlementAction)
            => $"Unable to {settlementAction} the Azure Service Bus message. No {nameof(ServiceBusReceivedMessage)} was contained in the message broker context, so the PeekLock delivery was not settled.";

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

        // Observation seam for the teardown paths: whether this receiver has already been disposed. Both
        // Dispose overloads fold into one guarded transition, and this is the only way to see it landed.
        internal bool IsDisposed => _disposedValue;

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
                    CloseWithoutAwaiting(_innerReceiver, "Failure closing the Azure Service Bus receiver while disposing it");
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
