using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The #361 observability sink for a faulted change feed. An Outbox Document that fails on EVERY pass re-throws
    /// forever: the change feed never checkpoints, the Lease Token never advances, and every later pending Outbox
    /// Document in that partition range stays undrained. This notifier is what makes that stall VISIBLE, and it is
    /// shaped to be handed to the Cosmos SDK's error-notification seam
    /// (<c>ChangeFeedProcessorBuilder.WithErrorNotification</c>), whose delegate takes the Lease Token and the fault
    /// and returns a <see cref="Task"/> — the only channel carrying an SDK-side lease or processor fault TOGETHER with
    /// the Lease Token, which the Outbox Relay core never sees.
    /// </summary>
    /// <remarks>
    /// TWO SINKS, one mechanism each, because their audiences are disjoint. The failure counter is OPT-IN and is
    /// recorded through <see cref="CosmosReliabilityDiagnostics.RecordDrainFailure"/>, inside that method's own
    /// off-guard (ADR-0010 R1). The log is the ALWAYS-ON channel: an application that opted into no meter would
    /// otherwise be left exactly as silent as the defect describes, so the fault is logged at
    /// <see cref="LogLevel.Error"/> through the structured message-template overload — never string interpolation,
    /// which would render the message before the level is checked.
    /// INVARIANT: a faulted attempt is NOT a fourth <c>DrainOutcomes</c> value; that vocabulary is closed and a
    /// document's outcome is already counted before a fault can be observed here.
    /// INVARIANT: this method NEVER throws. The Cosmos SDK invokes the notification delegate ON the change-feed pump,
    /// so a throw out of it would be a brand-new wedge of exactly the class this notifier exists to close — including
    /// when the application's own logging sink is what faulted. Observability may never break delivery.
    /// INVARIANT: the two sinks are ISOLATED from one another — one guarded block each — so a fault in either is
    /// swallowed WITHOUT suppressing the other. Sharing one guard would let the opt-in metric take the always-on log
    /// down with it, inverting the very guarantee this notifier exists to provide.
    /// The logger is OPTIONAL: a null logger is a silent no-op, so a host that resolved none still gets the metric.
    /// </remarks>
    internal sealed class RelayFailureNotifier
    {
        private readonly ILogger _logger;

        public RelayFailureNotifier(ILogger logger) => _logger = logger;

        /// <summary>
        /// Reports one change-feed fault, matching the shape of the Cosmos SDK's
        /// <c>Container.ChangeFeedMonitorErrorDelegate</c>.
        /// </summary>
        /// <param name="leaseToken">The change-feed lease the fault happened under.</param>
        /// <param name="exception">The fault the SDK observed.</param>
        internal Task OnChangeFeedErrorAsync(string leaseToken, Exception exception)
        {
            // INVARIANT: one guarded block PER SINK. A shared guard would make the ALWAYS-ON log conditional on the
            // OPT-IN metric: a MeterListener callback runs inline on this thread, so a broken collector's throw would
            // jump past the log and leave the stalled lease unreported in exactly the application that opted into no
            // logging change at all. The optional sink may not decide the fate of the mandatory one.
            try
            {
                CosmosReliabilityDiagnostics.RecordDrainFailure(leaseToken, exception);
            }
            catch (Exception)
            {
                // INVARIANT: a failure of this sink is swallowed rather than propagated, and it is never re-reported
                // through itself: the sink that just faulted could only fault again. Observability may never break
                // delivery.
            }

            try
            {
                _logger?.LogError(exception, "The Cosmos Outbox Relay change feed faulted on lease {LeaseToken}; that lease does not advance until the fault clears, so every later pending Outbox Document in its partition range stays undrained.", leaseToken);
            }
            catch (Exception)
            {
                // The same swallow, for the same reason: a broken logging sink may not wedge the change-feed pump.
            }

            return Task.CompletedTask;
        }
    }
}
