using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// The opt-in diagnostics half of <see cref="BrokeredMessageReceiver{TMessage}"/>, kept in its own partial so the
    /// lifecycle state machine, teardown gate and disposition monotonicity of the receiver proper are untouched by it.
    /// </summary>
    /// <remarks>
    /// WHY THE WORKER IS THE INSTRUMENTED SEAM. The cheaper-looking alternative is to instrument the received-message
    /// dispatcher, but that seam is DI-replaceable and
    /// <see cref="BrokeredMessageReceiver{TMessage}.DispatchReceivedMessageAsync"/> is virtual: Chatter.SqlChangeFeed's
    /// Change Feed receiver overrides it and never reaches the scoped dispatcher at all. Dispatcher-level
    /// instrumentation would also miss the deserialization (poisoned message) failure, every settlement, and the
    /// worker error ladder, all of which run outside dispatch. The per-delivery worker is the one seam every delivery
    /// crosses.
    /// INVARIANT: the off-guard is <see cref="BrokerDiagnostics.IsEnabled"/> — Chatter's OWN .NET
    /// <c>ActivitySource</c>/<c>Meter</c> subscriptions — never <see cref="Activity.Current"/>, which is non-null in
    /// any host running unrelated instrumentation and therefore does not mean Chatter diagnostics are on
    /// (ADR-0010 R1, R2). <see cref="Activity.Current"/> is never read here at all; the ambient-activity link is
    /// resolved inside <see cref="BrokerDiagnostics.StartReceive"/>, behind that same guard (ADR-0010 R3, D6).
    /// </remarks>
    public partial class BrokeredMessageReceiver<TMessage> where TMessage : class, IMessage
    {
        // INVARIANT: the per-delivery diagnostics scope travels down the worker's OWN async flow, never through a
        // field on this receiver. Up to MaxConcurrentCalls workers run concurrently over a single receiver instance,
        // so a shared field would cross-attribute one delivery's retries and settlement onto another delivery's span.
        // The scope object is mutated in place (rather than reassigned) because a deeper async flow cannot publish a
        // new AsyncLocal value back up to the worker that opened the span. Read and written ONLY inside the off-guard,
        // so an application that never opted in never touches it.
        private static readonly AsyncLocal<ReceiveDiagnosticsScope> _receiveDiagnostics = new AsyncLocal<ReceiveDiagnosticsScope>();

        /// <summary>
        /// One delivery's receive span and the number of Recovery attempts made against it.
        /// </summary>
        private sealed class ReceiveDiagnosticsScope
        {
            internal ReceiveDiagnosticsScope(Activity activity) => Activity = activity;

            /// <summary>The receive span, or <c>null</c> when only metrics were subscribed to or the span was sampled out.</summary>
            internal Activity Activity { get; }

            /// <summary>How many times Recovery has attempted this delivery.</summary>
            internal int Attempts;
        }

        // INVARIANT: ADR-0010 R1/R4 — the off-guard is evaluated before any argument is constructed, and the off path
        // returns the ORIGINAL worker Task rather than awaiting it, so no async state machine, no trace-context
        // extraction, no start timestamp and no allocation are added for an application that never opted in.
        private Task RunProcessingWorkerAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken workerToken)
        {
            if (!BrokerDiagnostics.IsEnabled)
            {
                return ProcessReceivedMessageWorkerAsync(messageContext, transactionContext, workerToken);
            }

            return RunProcessingWorkerWithDiagnosticsAsync(messageContext, transactionContext, workerToken);
        }

        // INVARIANT: the span is opened at worker ENTRY — before the body is deserialized and before any handler can
        // Forward or Reply — so the Trace Context those outbound paths inject is this delivery's receive span and the
        // producer's context has already been extracted by the time anything can overwrite it on the shared inbound
        // context dictionary.
        // INVARIANT: the `using` scope stops the span in a finally on EVERY path. The worker runs fire-and-forget
        // relative to the receive loop, so an unstopped Activity would remain current on a pooled thread and leak into
        // a later delivery.
        // INVARIANT: one span per DELIVERY, not per attempt. Recovery WRAPS dispatch, so every retry for this delivery
        // happens inside this span and the attempt count is a tag on it (ADR-0010 D7).
        private async Task RunProcessingWorkerWithDiagnosticsAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken workerToken)
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            var messagingSystem = _options.InfrastructureType;
            var inboundMessage = messageContext?.BrokeredMessage;

            using (var activity = BrokerDiagnostics.StartReceive(messagingSystem, BrokerDiagnostics.OperationTypes.Receive, this.MessageReceiverPath, inboundMessage?.MessageId, inboundMessage?.MessageContext))
            {
                var receiveDiagnostics = new ReceiveDiagnosticsScope(activity);
                _receiveDiagnostics.Value = receiveDiagnostics;

                try
                {
                    await ProcessReceivedMessageWorkerAsync(messageContext, transactionContext, workerToken).ConfigureAwait(false);
                    BrokerDiagnostics.RecordReceive(startTimestamp, messagingSystem, BrokerDiagnostics.OperationTypes.Receive, this.MessageReceiverPath, null);
                }
                catch (Exception deliveryError)
                {
                    // The worker's own error ladder settles and swallows every expected fault, so reaching here means
                    // an unexpected escape. Record it and let it propagate unchanged — diagnostics never swallow.
                    BrokerDiagnostics.RecordFailure(activity, deliveryError);
                    BrokerDiagnostics.RecordReceive(startTimestamp, messagingSystem, BrokerDiagnostics.OperationTypes.Receive, this.MessageReceiverPath, deliveryError);
                    throw;
                }
                finally
                {
                    activity?.SetTag(BrokerDiagnostics.ReceiveAttempts, receiveDiagnostics.Attempts);
                    _receiveDiagnostics.Value = null;
                }
            }
        }

        /// <summary>
        /// Counts one Recovery attempt against the delivery in flight, and adds a retry span event for every attempt
        /// after the first.
        /// </summary>
        /// <remarks>The event is added only when <see cref="Activity.IsAllDataRequested"/> is <c>true</c>, so a
        /// sampled-out span pays nothing to construct it (ADR-0010 D7).</remarks>
        private static void CountReceiveAttempt()
        {
            if (!BrokerDiagnostics.Source.HasListeners())
            {
                return;
            }

            var receiveDiagnostics = _receiveDiagnostics.Value;

            if (receiveDiagnostics is null)
            {
                return;
            }

            receiveDiagnostics.Attempts++;

            var activity = receiveDiagnostics.Activity;

            if (receiveDiagnostics.Attempts <= 1 || activity is null || !activity.IsAllDataRequested)
            {
                return;
            }

            activity.AddEvent(new ActivityEvent(BrokerDiagnostics.ReceiveRetryEventName, tags: new ActivityTagsCollection { { BrokerDiagnostics.ReceiveAttempts, receiveDiagnostics.Attempts } }));
        }

        /// <summary>
        /// Records how the delivery in flight was settled, on its receive span.
        /// </summary>
        /// <param name="settlement">One of <see cref="BrokerDiagnostics.Settlements"/>.</param>
        private static void RecordReceiveSettlement(string settlement)
        {
            if (!BrokerDiagnostics.Source.HasListeners())
            {
                return;
            }

            BrokerDiagnostics.RecordSettlement(_receiveDiagnostics.Value?.Activity, settlement);
        }

        /// <summary>
        /// Records the failure that ended the delivery in flight together with the settlement Chatter answered it
        /// with, on its receive span.
        /// </summary>
        /// <param name="exception">The failure that ended the delivery.</param>
        /// <param name="settlement">One of <see cref="BrokerDiagnostics.Settlements"/>.</param>
        /// <remarks>Recorded at the ladder site that CHOOSES the settlement rather than after the settlement call:
        /// every settlement helper is best-effort and reports its own failure through the receiver's logging, so the
        /// tag records the settlement Chatter answered the failure with.</remarks>
        private static void RecordReceiveFailure(Exception exception, string settlement)
        {
            if (!BrokerDiagnostics.Source.HasListeners())
            {
                return;
            }

            var activity = _receiveDiagnostics.Value?.Activity;
            BrokerDiagnostics.RecordFailure(activity, exception);
            BrokerDiagnostics.RecordSettlement(activity, settlement);
        }
    }
}
