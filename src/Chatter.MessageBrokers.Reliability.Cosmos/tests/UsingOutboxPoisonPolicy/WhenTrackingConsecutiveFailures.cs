using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingOutboxPoisonPolicy
{
    /// <summary>
    /// Characterizes the #361 opt-in poison policy's per-document-id consecutive-failure counting: it is OFF by default
    /// (threshold 0 never elects a document, so a relay configured without it behaves exactly as it does today), it
    /// elects only on the Nth CONSECUTIVE failure of the SAME document id, a successful drain resets that id's count,
    /// and the set of tracked ids is BOUNDED — past the cap a new id is simply not tracked, so a long-lived relay under
    /// transient failures degrades to today's fail-closed behavior rather than to unbounded memory.
    /// </summary>
    public class WhenTrackingConsecutiveFailures
    {
        private const string PoisonStatusValue = "poisoned";

        private static OutboxPoisonPolicy PolicyWith(int poisonAfterConsecutiveFailures)
            => new OutboxPoisonPolicy(poisonAfterConsecutiveFailures, PoisonStatusValue);

        [Fact]
        public void MustNeverElectWhenThresholdIsZero()
        {
            // The DEFAULT. A relay that never opted in must behave exactly as it does today: every failure re-throws and
            // the document stays pending, no matter how many times it fails.
            OutboxPoisonPolicy policy = PolicyWith(0);

            bool electedOnAnyFailure = Enumerable.Range(0, 25).Any(_ => policy.RecordFailure("outbox:msg-1"));

            policy.IsEnabled.Should().BeFalse("a zero threshold is the OFF switch, not a poison-on-first-failure switch");
            electedOnAnyFailure.Should().BeFalse("with the policy off, counting never advances and no document is ever elected");
        }

        [Fact]
        public void MustElectOnlyOnTheNthConsecutiveFailureOfTheSameDocument()
        {
            OutboxPoisonPolicy policy = PolicyWith(2);

            policy.RecordFailure("outbox:msg-x").Should().BeFalse("the first failure of a document is indistinguishable from a transient one");
            policy.RecordFailure("outbox:msg-x").Should().BeTrue("the second consecutive failure reaches the configured threshold");
        }

        [Fact]
        public void MustResetTheCountOfADocumentThatDrainedSuccessfully()
        {
            // An INTERMITTENT failure must never accumulate to a give-up: a document that failed once and then drained
            // starts over, so it takes a fresh run of N consecutive failures to elect it.
            OutboxPoisonPolicy policy = PolicyWith(2);
            policy.RecordFailure("outbox:msg-x");

            policy.RecordSuccess("outbox:msg-x");

            policy.RecordFailure("outbox:msg-x").Should().BeFalse("a successful drain reset the document's consecutive-failure count to zero");
        }

        [Fact]
        public void MustNotAdvanceADocumentsCountOnAnotherDocumentsFailure()
        {
            OutboxPoisonPolicy policy = PolicyWith(2);
            policy.RecordFailure("outbox:msg-x");

            policy.RecordFailure("outbox:msg-y").Should().BeFalse("a different document's failure is counted against that document only");

            policy.RecordFailure("outbox:msg-x").Should().BeTrue("only msg-x's OWN failures advance msg-x's count, and this is its second");
        }

        [Fact]
        public void MustNotTrackANewDocumentPastTheTrackedIdCapacity()
        {
            // The bound is load-bearing: an unbounded failure map grows forever on a long-lived relay under transient
            // failures. Past the cap a NEW id is simply not tracked, so it can never be elected and the relay degrades to
            // today's fail-closed behavior (re-throw, stay pending) rather than to unbounded memory.
            OutboxPoisonPolicy policy = PolicyWith(2);
            foreach (int index in Enumerable.Range(0, OutboxPoisonPolicy.TrackedDocumentCapacity))
            {
                policy.RecordFailure($"outbox:msg-{index}");
            }

            policy.RecordFailure("outbox:msg-overflow").Should().BeFalse("a new id past the cap is not tracked, so its first failure cannot be its Nth");
            policy.RecordFailure("outbox:msg-overflow").Should().BeFalse("an untracked id never accumulates, so it is never elected");

            policy.RecordFailure("outbox:msg-0").Should().BeTrue("the cap refuses only NEW ids; an already-tracked document still advances to the threshold");
        }
    }
}
