using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingOutboxGiveUpPolicy
{
    /// <summary>
    /// Characterizes the give-up policy's per-document-IDENTITY consecutive-failure counting across BOTH give-up kinds.
    /// The opt-in #361 poison arm is OFF by default (threshold 0 never elects a document, so a relay configured without
    /// it behaves exactly as it does today) and elects only on the Nth CONSECUTIVE failure of the SAME identity — the
    /// document id AND the logical partition it lives in, because two documents sharing a MessageId in different
    /// partitions are DIFFERENT items and neither may be given up on for the other's failures. The post-publish arm is
    /// ALWAYS ON and cannot be switched off: once ANY pass in a streak PUBLISHED, the streak is capped by the
    /// unconfirmed-publish cap and can only ever terminate as published-unconfirmed, never as poisoned. A successful
    /// drain resets that identity's count AND its published flag, and the set of tracked identities is BOUNDED: past the
    /// cap a new identity is simply not tracked, so a long-lived relay under transient failures degrades to today's
    /// fail-closed behavior rather than to unbounded memory.
    /// </summary>
    public class WhenTrackingConsecutiveFailures
    {
        private const string PoisonStatusValue = "poisoned";
        private const string UnconfirmedStatusValue = "published-unconfirmed";
        private const string TenantId = "tenant-1";
        private const int UnconfirmedPublishCap = 5;

        private static OutboxGiveUpPolicy PolicyWith(int poisonAfterConsecutiveFailures, int giveUpAfterUnconfirmedPublishes = UnconfirmedPublishCap)
            => new OutboxGiveUpPolicy(poisonAfterConsecutiveFailures, PoisonStatusValue, giveUpAfterUnconfirmedPublishes, UnconfirmedStatusValue);

        // Mirrors what the host counts on: the document's own id PLUS the partition key recovered for it, built the SAME
        // way CosmosOutboxRelay.RecoverPartitionKey builds the one the give-up patch targets.
        private static OutboxGiveUpPolicy.OutboxDocumentIdentity Identity(string documentId, string tenantId = TenantId)
            => new OutboxGiveUpPolicy.OutboxDocumentIdentity(documentId, new PartitionKeyBuilder().Add(tenantId).Build());

        [Fact]
        public void MustNeverElectWhenThresholdIsZero()
        {
            // The DEFAULT for the PRE-publish arm. A relay that never opted into the poison policy must behave exactly as
            // it does today: every pre-publish failure re-throws and the document stays pending, no matter how many times
            // it fails. Nothing was published, so nothing about the cost this policy bounds is at stake.
            OutboxGiveUpPolicy policy = PolicyWith(0);

            bool electedOnAnyFailure = Enumerable.Range(0, 25)
                .Any(_ => policy.RecordFailure(Identity("outbox:msg-1"), messagePublished: false) != GiveUpKind.None);

            policy.IsPoisonEnabled.Should().BeFalse("a zero threshold is the OFF switch, not a poison-on-first-failure switch");
            electedOnAnyFailure.Should().BeFalse("with the poison policy off, a PURE pre-publish streak is never elected, at any length");
        }

        [Fact]
        public void MustElectOnlyOnTheNthConsecutiveFailureOfTheSameDocument()
        {
            OutboxGiveUpPolicy policy = PolicyWith(2);

            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false).Should().Be(GiveUpKind.None, "the first failure of a document is indistinguishable from a transient one");
            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false).Should().Be(GiveUpKind.Poison, "the second consecutive failure of the SAME identity reaches the configured threshold");
        }

        [Fact]
        public void MustCountSeparatelyForOneIdInDifferentPartitions()
        {
            // The id alone is UNDER-QUALIFIED: two Outbox Documents sharing a MessageId in different logical partitions
            // are different items, and the patch that gives one up is keyed on id AND partition key. Counting them in one
            // slot would give up on a document after FEWER failures than the configured threshold.
            OutboxGiveUpPolicy policy = PolicyWith(2);

            policy.RecordFailure(Identity("outbox:msg-x", "tenant-a"), messagePublished: false).Should().Be(GiveUpKind.None, "this identity has failed once");
            policy.RecordFailure(Identity("outbox:msg-x", "tenant-b"), messagePublished: false).Should().Be(GiveUpKind.None, "the other partition's document has also failed only once; it does not inherit the first one's count");

            policy.RecordFailure(Identity("outbox:msg-x", "tenant-a"), messagePublished: false).Should().Be(GiveUpKind.Poison, "this is the second consecutive failure of THIS identity");
        }

        [Fact]
        public void MustResetTheCountOfADocumentThatDrainedSuccessfully()
        {
            // An INTERMITTENT failure must never accumulate to a give-up: a document that failed once and then drained
            // starts over, so it takes a fresh run of N consecutive failures to elect it.
            OutboxGiveUpPolicy policy = PolicyWith(2);
            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false);

            policy.RecordSuccess(Identity("outbox:msg-x"));

            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false).Should().Be(GiveUpKind.None, "a successful drain reset the document's consecutive-failure count to zero");
        }

        [Fact]
        public void MustClearOnlyTheIdentityThatDrainedSuccessfully()
        {
            OutboxGiveUpPolicy policy = PolicyWith(2);
            policy.RecordFailure(Identity("outbox:msg-x", "tenant-a"), messagePublished: false);
            policy.RecordFailure(Identity("outbox:msg-x", "tenant-b"), messagePublished: false);

            policy.RecordSuccess(Identity("outbox:msg-x", "tenant-a"));

            policy.RecordFailure(Identity("outbox:msg-x", "tenant-b"), messagePublished: false).Should().Be(GiveUpKind.Poison, "the other partition's drain succeeded, which says nothing about this identity's run of failures");
            policy.RecordFailure(Identity("outbox:msg-x", "tenant-a"), messagePublished: false).Should().Be(GiveUpKind.None, "this identity's own successful drain reset its count to zero");
        }

        [Fact]
        public void MustNotAdvanceADocumentsCountOnAnotherDocumentsFailure()
        {
            OutboxGiveUpPolicy policy = PolicyWith(2);
            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false);

            policy.RecordFailure(Identity("outbox:msg-y"), messagePublished: false).Should().Be(GiveUpKind.None, "a different document's failure is counted against that document only");

            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false).Should().Be(GiveUpKind.Poison, "only msg-x's OWN failures advance msg-x's count, and this is its second");
        }

        [Fact]
        public void MustNotTrackANewIdentityPastTheTrackedDocumentCapacity()
        {
            // The bound is load-bearing: an unbounded failure map grows forever on a long-lived relay under transient
            // failures. Past the cap a NEW identity is simply not tracked, so it can never be elected and the relay
            // degrades to today's fail-closed behavior (re-throw, stay pending) rather than to unbounded memory.
            OutboxGiveUpPolicy policy = PolicyWith(2);
            foreach (int index in Enumerable.Range(0, OutboxGiveUpPolicy.TrackedDocumentCapacity))
            {
                policy.RecordFailure(Identity($"outbox:msg-{index}"), messagePublished: false);
            }

            policy.RecordFailure(Identity("outbox:msg-overflow"), messagePublished: false).Should().Be(GiveUpKind.None, "a new identity past the cap is not tracked, so its first failure cannot be its Nth");
            policy.RecordFailure(Identity("outbox:msg-overflow"), messagePublished: false).Should().Be(GiveUpKind.None, "an untracked identity never accumulates, so it is never elected");

            policy.RecordFailure(Identity("outbox:msg-0"), messagePublished: false).Should().Be(GiveUpKind.Poison, "the cap refuses only NEW identities; an already-tracked document still advances to the threshold");
        }

        [Fact]
        public void MustElectUnconfirmedPublishAtExactlyTheCapForAPurePostPublishStreak()
        {
            // The ALWAYS-ON brake. Publishing forever is the unbounded-cost escape: every pass is a broker publish plus
            // request units plus downstream consumer work. With the poison policy OFF the streak is still capped.
            OutboxGiveUpPolicy policy = PolicyWith(0, giveUpAfterUnconfirmedPublishes: UnconfirmedPublishCap);

            foreach (int failure in Enumerable.Range(1, UnconfirmedPublishCap - 1))
            {
                policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: true)
                      .Should().Be(GiveUpKind.None, $"failure {failure} is below the cap of {UnconfirmedPublishCap}");
            }

            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: true)
                  .Should().Be(GiveUpKind.UnconfirmedPublish, "the cap is reached, and a streak that published can only ever terminate as published-unconfirmed");
        }

        [Fact]
        public void MustElectUnconfirmedPublishOnTheMthFailureOfAMixedStreak()
        {
            // THE MIXED-SEQUENCE ESCAPE. With two SEPARATE counters a document alternating pre-publish and post-publish
            // failures forever would reach NEITHER threshold — and that escape is the PUBLISHING one, so it is the
            // unbounded-cost one. ONE counter with a sticky published flag makes it unrepresentable: the Mth failure of
            // the streak elects, no matter how the phases interleave.
            OutboxGiveUpPolicy policy = PolicyWith(0, giveUpAfterUnconfirmedPublishes: UnconfirmedPublishCap);

            foreach (int failure in Enumerable.Range(1, UnconfirmedPublishCap - 1))
            {
                policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: failure % 2 == 0)
                      .Should().Be(GiveUpKind.None, $"failure {failure} of the alternating streak is below the cap");
            }

            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: UnconfirmedPublishCap % 2 == 0)
                  .Should().Be(GiveUpKind.UnconfirmedPublish, "the ONE counter reaches the cap on the Mth failure regardless of which phase each failure was in");
        }

        [Fact]
        public void MustNeverElectPoisonOnceAStreakHasPublished()
        {
            // THE HONESTY INVARIANT. With two counters a mixed streak could trip the poison counter and stamp 'poisoned'
            // — 'never delivered' — on a document that WAS published, sending an operator hunting for a message that was
            // never lost. The sticky flag makes that lie unrepresentable: the terminal kind follows the flag, so once any
            // pass published, poison is off the table even though the poison threshold is far lower than the cap.
            OutboxGiveUpPolicy policy = PolicyWith(2, giveUpAfterUnconfirmedPublishes: UnconfirmedPublishCap);

            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: true).Should().Be(GiveUpKind.None, "the first failure published, so the streak is now capped by the unconfirmed cap");
            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false).Should().Be(GiveUpKind.None, "this is the second failure, which WOULD have tripped the poison threshold had the published phase not been sticky");
            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false).Should().Be(GiveUpKind.None, "still below the unconfirmed cap the sticky flag selected");
            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false).Should().Be(GiveUpKind.None, "still below the unconfirmed cap the sticky flag selected");

            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false)
                  .Should().Be(GiveUpKind.UnconfirmedPublish, "a streak that EVER published can only terminate as published-unconfirmed — never as poisoned, which would claim the message was never delivered");
        }

        [Fact]
        public void MustClearTheStickyPublishedFlagOnASuccessfulDrain()
        {
            // The reset clears the COUNT and the FLAG together. Otherwise a document that once published and later failed
            // pre-publish would carry the published phase across a successful drain and be given up on as
            // published-unconfirmed for a message that never went out on THIS streak.
            OutboxGiveUpPolicy policy = PolicyWith(2, giveUpAfterUnconfirmedPublishes: UnconfirmedPublishCap);
            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: true);

            policy.RecordSuccess(Identity("outbox:msg-x"));

            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false).Should().Be(GiveUpKind.None, "the successful drain started a fresh streak");
            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: false)
                  .Should().Be(GiveUpKind.Poison, "the fresh streak never published, so it is governed by the poison threshold again");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void MustRejectAnUnconfirmedPublishCapThatIsNotPositive(int giveUpAfterUnconfirmedPublishes)
        {
            // THE BRAKE HAS NO OFF SWITCH. A cap of 0 as an off switch would make an unbounded republish storm a
            // REPRESENTABLE configuration; rejecting it at construction makes that storm unconstructable.
            Action construct = () => PolicyWith(0, giveUpAfterUnconfirmedPublishes);

            construct.Should().Throw<ArgumentException>("an unbounded republish storm must be unconstructable, not merely discouraged")
                     .Which.ParamName.Should().Be("giveUpAfterUnconfirmedPublishes");
        }

        [Fact]
        public void MustNotTrackAnythingUntilAFailureIsRecorded()
        {
            // What lets a healthy drain stay free: the hosts skip building a document identity for a successful drain
            // while nothing at all is tracked, so an all-healthy relay pays no id read and no partition-key recovery.
            OutboxGiveUpPolicy policy = PolicyWith(0);

            policy.HasTrackedFailures.Should().BeFalse("no failure has been recorded, so there is no slot any success could clear");

            policy.RecordFailure(Identity("outbox:msg-x"), messagePublished: true);

            policy.HasTrackedFailures.Should().BeTrue("a mid-streak identity is tracked until it is elected or drains successfully");
        }
    }
}
