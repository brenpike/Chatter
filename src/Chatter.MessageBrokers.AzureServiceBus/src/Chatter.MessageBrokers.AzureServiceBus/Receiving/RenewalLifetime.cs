using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Owns ONE delivery's renewal for its whole life: the cancellation source that ends it, the loop that runs
    /// it, its exit from the registry's tracking, and the report of a renewal that failed.
    /// </summary>
    /// <remarks>
    /// INVARIANT: every obligation this renewal owes is discharged in the <c>finally</c> of ONE async flow, and
    /// <see cref="Completion"/> IS that flow's task — so a step that throws can neither skip a cleanup nor leave
    /// a teardown holding a completion nothing will complete. The cancellation source is constructed, cancelled
    /// and disposed here and is reachable from nowhere else, which is what makes that guarantee hold for every
    /// caller rather than only for the ones that remembered to clean up.
    /// </remarks>
    internal sealed class RenewalLifetime
    {
        private readonly CancellationTokenSource _renewalSource = new CancellationTokenSource();
        private readonly string _description;
        private readonly ILogger _logger;

        internal RenewalLifetime(string description, ILogger logger)
        {
            _description = description;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Completes — never faults, never cancels — once this renewal has ENDED and released everything it owns.
        /// </summary>
        internal Task Completion { get; private set; }

        /// <summary>
        /// Whether this renewal's delivery has been released or its receiver closed, so the delivery is no longer
        /// in flight however long the loop takes to end. Written and read under the owning registry's lock.
        /// </summary>
        internal bool Stopped { get; set; }

        /// <summary>
        /// Runs <paramref name="renewalLoop"/> on this renewal's own cancellation, calling
        /// <paramref name="onEnded"/> as the renewal leaves its owner's tracking.
        /// </summary>
        /// <remarks>
        /// A loop that never awaits ends INSIDE this call, running <paramref name="onEnded"/> with it — which is
        /// why an owner must record the renewal before beginning it.
        /// </remarks>
        internal void Begin(Func<CancellationToken, Task> renewalLoop, Action onEnded)
            => Completion = RunToEndAsync(renewalLoop, onEnded);

        /// <summary>
        /// Ends this renewal, and NEVER throws.
        /// </summary>
        /// <remarks>
        /// CONTRACT BOUNDARY, not case enumeration: both callers are paths that must not raise — a delivery's
        /// release (ADR-0014/ADR-0020) and the close every caller of it awaits — while
        /// <see cref="CancellationTokenSource.Cancel()"/> raises whatever a registration raises and rejects a
        /// source already disposed. Cancelling is all this does: everything the renewal owns is released by the
        /// flow <see cref="Begin"/> started, so a cancel that fails here skips no cleanup, and the token is
        /// signalled before any registration runs, so the loop ends either way.
        /// </remarks>
        internal void End()
        {
            try
            {
                _renewalSource.Cancel();
            }
            catch (Exception endFailure)
            {
                _logger.LogTrace(endFailure, $"Ending the Azure Service Bus lock renewal for {_description} raised after the renewal was signalled to end");
            }
        }

        private async Task RunToEndAsync(Func<CancellationToken, Task> renewalLoop, Action onEnded)
        {
            try
            {
                try
                {
                    await renewalLoop(_renewalSource.Token).ConfigureAwait(false);
                }
                finally
                {
                    // INVARIANT: tracking is left BEFORE the source is disposed, and the disposal is owed even if
                    // leaving tracking raises — being tracked means a renewal has not ended, so a cleanup ordered
                    // the other way could leave an ended renewal recorded as running forever.
                    try
                    {
                        onEnded();
                    }
                    finally
                    {
                        _renewalSource.Dispose();
                    }
                }
            }
            catch (Exception renewalFailure)
            {
                _logger.LogWarning(renewalFailure, $"Failure renewing the Azure Service Bus lock for {_description}");
            }
        }
    }
}
