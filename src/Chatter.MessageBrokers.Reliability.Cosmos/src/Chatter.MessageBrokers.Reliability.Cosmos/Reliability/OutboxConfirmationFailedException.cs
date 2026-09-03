using System;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The internal carrier the Outbox Relay raises when a Confirmation Failure ends a drain: the message PUBLISHED
    /// and the delivered stamp did not land, so the batch is not checkpointed and the same document publishes again
    /// on every pass (#416). It wraps the underlying fault and exists only to survive the stack unwind up to the
    /// host, which is the one component that knows the Lease Token the failure happened under.
    /// </summary>
    /// <remarks>
    /// THIS IS NOT EXCEPTION-TYPE SNIFFING. Nothing here inspects a provider exception and infers what phase it came
    /// from. The type is MINTED by the relay's OWN control flow, at a single site reachable only AFTER a successful
    /// publish, exactly as the drain phase is derived from whether the dispatch returned rather than from what it
    /// threw. The relay is the thing that publishes, so it is the only thing that knows.
    /// INVARIANT: it NEVER escapes this module. The host unwraps it and rethrows the INNER fault through
    /// <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/>, so the caller observes the original
    /// exception with its original stack trace and the <c>error.type</c> on the shipped drain-failure count stays
    /// byte-identical to what it reported before this carrier existed.
    /// INVARIANT: the underlying fault is REQUIRED. A carrier with nothing to carry would leave the host with no
    /// failure to rethrow and would surface this internal type where the original belonged, so it is rejected at
    /// construction rather than guarded against at every unwrap site.
    /// </remarks>
    internal sealed class OutboxConfirmationFailedException : Exception
    {
        internal OutboxConfirmationFailedException(Exception confirmationFailure)
            : base("The Cosmos Outbox Relay published an Outbox Document but could not mark it delivered. The change-feed batch is not checkpointed, so the document re-surfaces and publishes again; this carries the confirmation failure up to the host, which knows the lease it happened under.",
                   confirmationFailure ?? throw new ArgumentNullException(nameof(confirmationFailure)))
        {
        }
    }
}
