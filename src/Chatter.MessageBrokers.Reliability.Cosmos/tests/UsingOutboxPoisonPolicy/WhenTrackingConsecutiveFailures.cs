using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingOutboxPoisonPolicy
{
    /// <summary>
    /// Characterizes the #361 opt-in poison policy's per-document-IDENTITY consecutive-failure counting: it is OFF by
    /// default (threshold 0 never elects a document, so a relay configured without it behaves exactly as it does today),
    /// it elects only on the Nth CONSECUTIVE failure of the SAME identity — the document id AND the logical partition it
    /// lives in, because two documents sharing a MessageId in different partitions are DIFFERENT items and neither may be
    /// given up on for the other's failures — a successful drain resets that identity's count, and the set of tracked
    /// identities is BOUNDED: past the cap a new identity is simply not tracked, so a long-lived relay under transient
    /// failures degrades to today's fail-closed behavior rather than to unbounded memory.
    /// </summary>
    public class WhenTrackingConsecutiveFailures
    {
        private const string PoisonStatusValue = "poisoned";
        private const string TenantId = "tenant-1";

        private static OutboxPoisonPolicy PolicyWith(int poisonAfterConsecutiveFailures)
            => new OutboxPoisonPolicy(poisonAfterConsecutiveFailures, PoisonStatusValue);

        // Mirrors what the host counts on: the document's own id PLUS the partition key recovered for it, built the SAME
        // way CosmosOutboxRelay.RecoverPartitionKey builds the one the poison/delivered patch targets.
        private static OutboxPoisonPolicy.OutboxDocumentIdentity Identity(string documentId, string tenantId = TenantId)
            => new OutboxPoisonPolicy.OutboxDocumentIdentity(documentId, new PartitionKeyBuilder().Add(tenantId).Build());

        [Fact]
        public void MustNeverElectWhenThresholdIsZero()
        {
            // The DEFAULT. A relay that never opted in must behave exactly as it does today: every failure re-throws and
            // the document stays pending, no matter how many times it fails.
            OutboxPoisonPolicy policy = PolicyWith(0);

            bool electedOnAnyFailure = Enumerable.Range(0, 25).Any(_ => policy.RecordFailure(Identity("outbox:msg-1")));

            policy.IsEnabled.Should().BeFalse("a zero threshold is the OFF switch, not a poison-on-first-failure switch");
            electedOnAnyFailure.Should().BeFalse("with the policy off, counting never advances and no document is ever elected");
        }

        [Fact]
        public void MustElectOnlyOnTheNthConsecutiveFailureOfTheSameDocument()
        {
            OutboxPoisonPolicy policy = PolicyWith(2);

            policy.RecordFailure(Identity("outbox:msg-x")).Should().BeFalse("the first failure of a document is indistinguishable from a transient one");
            policy.RecordFailure(Identity("outbox:msg-x")).Should().BeTrue("the second consecutive failure of the SAME identity reaches the configured threshold");
        }

        [Fact]
        public void MustCountSeparatelyForOneIdInDifferentPartitions()
        {
            // The id alone is UNDER-QUALIFIED: two Outbox Documents sharing a MessageId in different logical partitions
            // are different items, and the patch that gives one up is keyed on id AND partition key. Counting them in one
            // slot would give up on a document after FEWER failures than the configured threshold.
            OutboxPoisonPolicy policy = PolicyWith(2);

            policy.RecordFailure(Identity("outbox:msg-x", "tenant-a")).Should().BeFalse("this identity has failed once");
            policy.RecordFailure(Identity("outbox:msg-x", "tenant-b")).Should().BeFalse("the other partition's document has also failed only once; it does not inherit the first one's count");

            policy.RecordFailure(Identity("outbox:msg-x", "tenant-a")).Should().BeTrue("this is the second consecutive failure of THIS identity");
        }

        [Fact]
        public void MustResetTheCountOfADocumentThatDrainedSuccessfully()
        {
            // An INTERMITTENT failure must never accumulate to a give-up: a document that failed once and then drained
            // starts over, so it takes a fresh run of N consecutive failures to elect it.
            OutboxPoisonPolicy policy = PolicyWith(2);
            policy.RecordFailure(Identity("outbox:msg-x"));

            policy.RecordSuccess(Identity("outbox:msg-x"));

            policy.RecordFailure(Identity("outbox:msg-x")).Should().BeFalse("a successful drain reset the document's consecutive-failure count to zero");
        }

        [Fact]
        public void MustClearOnlyTheIdentityThatDrainedSuccessfully()
        {
            OutboxPoisonPolicy policy = PolicyWith(2);
            policy.RecordFailure(Identity("outbox:msg-x", "tenant-a"));
            policy.RecordFailure(Identity("outbox:msg-x", "tenant-b"));

            policy.RecordSuccess(Identity("outbox:msg-x", "tenant-a"));

            policy.RecordFailure(Identity("outbox:msg-x", "tenant-b")).Should().BeTrue("the other partition's drain succeeded, which says nothing about this identity's run of failures");
            policy.RecordFailure(Identity("outbox:msg-x", "tenant-a")).Should().BeFalse("this identity's own successful drain reset its count to zero");
        }

        [Fact]
        public void MustNotAdvanceADocumentsCountOnAnotherDocumentsFailure()
        {
            OutboxPoisonPolicy policy = PolicyWith(2);
            policy.RecordFailure(Identity("outbox:msg-x"));

            policy.RecordFailure(Identity("outbox:msg-y")).Should().BeFalse("a different document's failure is counted against that document only");

            policy.RecordFailure(Identity("outbox:msg-x")).Should().BeTrue("only msg-x's OWN failures advance msg-x's count, and this is its second");
        }

        [Fact]
        public void MustNotTrackANewIdentityPastTheTrackedDocumentCapacity()
        {
            // The bound is load-bearing: an unbounded failure map grows forever on a long-lived relay under transient
            // failures. Past the cap a NEW identity is simply not tracked, so it can never be elected and the relay
            // degrades to today's fail-closed behavior (re-throw, stay pending) rather than to unbounded memory.
            OutboxPoisonPolicy policy = PolicyWith(2);
            foreach (int index in Enumerable.Range(0, OutboxPoisonPolicy.TrackedDocumentCapacity))
            {
                policy.RecordFailure(Identity($"outbox:msg-{index}"));
            }

            policy.RecordFailure(Identity("outbox:msg-overflow")).Should().BeFalse("a new identity past the cap is not tracked, so its first failure cannot be its Nth");
            policy.RecordFailure(Identity("outbox:msg-overflow")).Should().BeFalse("an untracked identity never accumulates, so it is never elected");

            policy.RecordFailure(Identity("outbox:msg-0")).Should().BeTrue("the cap refuses only NEW identities; an already-tracked document still advances to the threshold");
        }
    }
}
