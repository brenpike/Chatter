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

            /// <summary>
            /// The fault that ended this delivery, or <c>null</c> when the delivery succeeded or ended in a
            /// shutdown cancellation.
            /// </summary>
            /// <remarks>
            /// The worker's error ladder settles an expected fault and then returns NORMALLY, so the worker's own
            /// success path cannot see it. Without this the receive metric would report every nacked, deadlettered
            /// and poisoned delivery as a successful operation, omitting the <c>error.type</c> attribute semconv
            /// v1.30.0 requires on a failed operation — visible to a metrics-only application, which has no span to
            /// read the failure off (ADR-0010 D4).
            /// Written ONLY by <see cref="RetainReceiveFailure"/>, from the exception filter that is the single
            /// choke point every ladder branch passes through, so no branch can settle a fault without retaining it
            /// (ADR-0010 D11).
            /// </remarks>
            internal Exception Failure;
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

                    // Returning normally does NOT mean the delivery succeeded: the worker's error ladder settles an
                    // expected fault and swallows it, so the fault retained at the worker's exception-filter choke
                    // point is read back off the scope here and carried into the metric as error.type. The span
                    // already carries it, recorded at that same choke point; this is what keeps the metrics-only
                    // half of the surface truthful.
                    BrokerDiagnostics.RecordReceive(startTimestamp, messagingSystem, BrokerDiagnostics.OperationTypes.Receive, this.MessageReceiverPath, receiveDiagnostics.Failure);
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
        /// <remarks>Called at the ladder site that CHOOSES the settlement rather than after the settlement call:
        /// every settlement helper is best-effort and reports its own failure through the receiver's logging, so
        /// the tag records the settlement Chatter ANSWERED with. A ladder site records only its settlement — the
        /// fault itself is recorded and retained once, at the worker's exception-filter choke point
        /// (<see cref="RetainReceiveFailure"/>), so a branch cannot report a settlement without a fault or a fault
        /// without a settlement by forgetting one of two calls (ADR-0010 D11).</remarks>
        private static void RecordReceiveSettlement(string settlement)
        {
            if (!BrokerDiagnostics.Source.HasListeners())
            {
                return;
            }

            BrokerDiagnostics.RecordSettlement(_receiveDiagnostics.Value?.Activity, settlement);
        }

        /// <summary>
        /// Retains the fault that ended the delivery in flight and records it on that delivery's receive span.
        /// </summary>
        /// <param name="deliveryFault">The fault leaving the worker's processing try block.</param>
        /// <param name="workerToken">The worker's cancellation token, which identifies a shutdown cancellation.</param>
        /// <returns>Always <c>false</c>, so the exception filter this runs in NEVER admits the exception.</returns>
        /// <remarks>
        /// INVARIANT: this is the ONE place the diagnostics half observes a delivery fault, and it is invoked from
        /// an exception FILTER on the worker's processing try block. A filter runs in the first pass of exception
        /// handling — before the stack unwinds and before any catch body — so every fault leaving that block is
        /// retained BEFORE the error ladder chooses a branch. Retention therefore cannot be forgotten by a ladder
        /// branch, present or future; a branch records only the settlement it CHOSE, through
        /// <see cref="RecordReceiveSettlement"/> (ADR-0010 D11).
        /// The outer guard is <see cref="BrokerDiagnostics.IsEnabled"/> rather than the .NET <c>ActivitySource</c>
        /// guard the span work needs, because the fault must also be RETAINED for the metric a tracing-free
        /// application subscribed to; <see cref="BrokerDiagnostics.RecordFailure"/> carries its own
        /// <c>HasListeners</c> guard, so an application with only a .NET <c>MeterListener</c> still starts no span
        /// (ADR-0010 R1). Nothing is allocated and no state machine is entered on the opted-out path, and the
        /// filter is reached only when a delivery has already faulted.
        /// </remarks>
        private static bool RetainReceiveFailure(Exception deliveryFault, CancellationToken workerToken)
        {
            if (!BrokerDiagnostics.IsEnabled || IsShutdownCancellation(deliveryFault, workerToken))
            {
                return false;
            }

            var receiveDiagnostics = _receiveDiagnostics.Value;

            if (receiveDiagnostics is null)
            {
                return false;
            }

            receiveDiagnostics.Failure = deliveryFault;
            BrokerDiagnostics.RecordFailure(receiveDiagnostics.Activity, deliveryFault);

            return false;
        }

        /// <summary>
        /// Decides whether <paramref name="deliveryFault"/> is the receiver being shut down underneath an in-flight
        /// delivery rather than a failed receive.
        /// </summary>
        /// <remarks>
        /// DECIDED, not incidental (ADR-0010 D11): a shutdown-cancelled delivery is NOT a failed receive, so it is
        /// neither retained for <c>error.type</c> nor marked on the span. Every deployment would otherwise emit a
        /// burst of failed receives — one per delivery in flight — and the resulting error-rate spike on every
        /// clean restart is worse than the lost cancellation signal, which the receiver already logs.
        /// The predicate deliberately MIRRORS the worker error ladder's own shutdown-swallow filters
        /// (<c>catch (OperationCanceledException) when (workerToken.IsCancellationRequested)</c> and its
        /// <c>ObjectDisposedException</c> twin), so "the ladder swallowed this as benign teardown" and "diagnostics
        /// did not count this as a failure" are one and the same condition. A cancellation raised while the worker
        /// token is NOT cancelled is a genuine failure, is settled by the ladder as one, and IS retained.
        /// </remarks>
        private static bool IsShutdownCancellation(Exception deliveryFault, CancellationToken workerToken)
            => workerToken.IsCancellationRequested
            && (deliveryFault is OperationCanceledException || deliveryFault is ObjectDisposedException);
    }
}
