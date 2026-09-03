#nullable enable annotations
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Concurrent;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// Which bounded outcome an Outbox Relay drain streak terminated in, or <see cref="None"/> while it has not
    /// terminated at all. The value follows the streak's PHASE, never the failure's type: a streak that never published
    /// can only ever be <see cref="Poison"/>, and one that published at least once can only ever be
    /// <see cref="UnconfirmedPublish"/>.
    /// </summary>
    internal enum GiveUpKind
    {
        /// <summary>The streak is still below the cap that governs it, so the failure propagates unchanged.</summary>
        None = 0,

        /// <summary>The opt-in #361 outcome: the streak NEVER published, so the document is stamped poisoned.</summary>
        Poison,

        /// <summary>The always-on outcome: the streak PUBLISHED, so the document is stamped published-unconfirmed.</summary>
        UnconfirmedPublish,
    }

    /// <summary>
    /// The escape from an Outbox Document that fails on every drain pass. The relay's fail-closed bias — throw, so the
    /// change feed never checkpoints, so the document re-surfaces — is correct for a TRANSIENT failure, but a document
    /// that fails on EVERY pass re-throws forever, and what that costs depends on WHERE in the drain it fails. This
    /// policy carries BOTH bounded outcomes, the status value each is stamped with, and the BOUNDED
    /// per-document-IDENTITY consecutive-failure counting that elects between them:
    /// <list type="bullet">
    /// <item>PRE-publish (nothing went out): the OPT-IN #361 poison arm. Its cost is a stalled lease — every later
    /// document in that partition range never drains — so opting out is a legitimate choice and it is OFF by default.</item>
    /// <item>POST-publish (the message already reached the broker but the delivered stamp failed): the ALWAYS-ON arm.
    /// Its cost is a broker publish plus request units plus downstream consumer work on EVERY pass, without limit, so
    /// there is no off switch — a threshold that is not positive is rejected at construction.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// ONE COUNTER, ONE COMPARISON, PLUS A STICKY FLAG. Two separate counters would leave a document alternating
    /// pre-publish and post-publish failures forever reaching NEITHER threshold — and that escape is the PUBLISHING one,
    /// so it is the unbounded-cost one. A single count whose cap is selected by a sticky "this streak published" flag
    /// makes that escape unrepresentable. The same flag settles the HONESTY question: a mixed streak can never be
    /// stamped poisoned — "never delivered" — for a message that actually went out.
    /// <para>
    /// The tracked-identity set is CAPPED at <see cref="TrackedDocumentCapacity"/>: past the cap a NEW identity is simply
    /// not tracked, so a long-lived relay under transient failures degrades to the fail-closed behavior rather than to
    /// unbounded memory. An entry is removed on election AND on a successful drain, so it holds only identities mid-streak.
    /// </para>
    /// </remarks>
    internal sealed class OutboxGiveUpPolicy
    {
        /// <summary>
        /// The maximum number of document IDENTITIES whose consecutive-failure streaks are tracked at once. A new identity
        /// arriving past this cap is NOT tracked, so it can never be elected — the memory the policy costs is bounded by
        /// construction.
        /// </summary>
        public const int TrackedDocumentCapacity = 1024;

        /// <summary>The default number of consecutive published-but-unconfirmed drains after which re-publishing stops.</summary>
        public const int DefaultGiveUpAfterUnconfirmedPublishes = 5;

        // INVARIANT: an entry exists ONLY for a document whose most recent drain FAILED and whose streak has not yet
        // reached the cap governing it. A successful drain removes the entry, and an elected document removes its own
        // entry (it is stamped non-pending, so the change feed never hands it back), so neither can leak a tracked identity.
        private readonly ConcurrentDictionary<OutboxDocumentIdentity, FailureStreak> _failureStreaksByIdentity = new ConcurrentDictionary<OutboxDocumentIdentity, FailureStreak>();

        public OutboxGiveUpPolicy(int poisonAfterConsecutiveFailures,
                                  string poisonStatusValue,
                                  int giveUpAfterUnconfirmedPublishes,
                                  string unconfirmedStatusValue)
        {
            if (poisonAfterConsecutiveFailures < 0)
            {
                throw new ArgumentException(
                    $"The consecutive-failure threshold cannot be negative (got {poisonAfterConsecutiveFailures}); use 0 to leave the poison policy off.",
                    nameof(poisonAfterConsecutiveFailures));
            }

            if (poisonAfterConsecutiveFailures > 0)
            {
                // An enabled poison arm MUST be able to move a given-up document out of pending, so its status value cannot
                // be empty nor equal the pending status — either would leave the document admitted forever, which is the
                // very head-of-line stall the arm exists to end.
                if (string.IsNullOrEmpty(poisonStatusValue))
                {
                    throw new ArgumentException(
                        "A poison status value is required and cannot be empty when the poison policy is enabled.", nameof(poisonStatusValue));
                }

                if (string.Equals(poisonStatusValue, CosmosOutboxDocument.StatusPending, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"The poison status value cannot equal the pending status '{CosmosOutboxDocument.StatusPending}'; a given-up document must be advanced OUT of pending or it would re-surface on the change feed forever.",
                        nameof(poisonStatusValue));
                }
            }

            // THE POST-PUBLISH BRAKE HAS NO OFF SWITCH. A threshold of 0 read as "off" would make an unbounded republish
            // storm — a broker publish plus request units plus downstream consumer work on every pass, forever — a
            // REPRESENTABLE configuration. Rejecting it here makes that storm unconstructable rather than discouraged.
            if (giveUpAfterUnconfirmedPublishes <= 0)
            {
                throw new ArgumentException(
                    $"The published-but-unconfirmed give-up threshold must be positive (got {giveUpAfterUnconfirmedPublishes}); there is no off switch, because leaving it off would re-publish an unconfirmable document forever.",
                    nameof(giveUpAfterUnconfirmedPublishes));
            }

            // Unlike the poison status this one is ALWAYS reachable, so it is validated unconditionally.
            if (string.IsNullOrEmpty(unconfirmedStatusValue))
            {
                throw new ArgumentException(
                    "A published-but-unconfirmed status value is required and cannot be empty.", nameof(unconfirmedStatusValue));
            }

            if (string.Equals(unconfirmedStatusValue, CosmosOutboxDocument.StatusPending, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The published-but-unconfirmed status value cannot equal the pending status '{CosmosOutboxDocument.StatusPending}'; a document the relay stopped re-publishing must be advanced OUT of pending or it would re-surface on the change feed forever.",
                    nameof(unconfirmedStatusValue));
            }

            PoisonAfterConsecutiveFailures = poisonAfterConsecutiveFailures;
            PoisonStatusValue = poisonStatusValue;
            GiveUpAfterUnconfirmedPublishes = giveUpAfterUnconfirmedPublishes;
            UnconfirmedStatusValue = unconfirmedStatusValue;
        }

        /// <summary>The number of CONSECUTIVE failures of one document identity after which a NEVER-PUBLISHED document is given up on. Zero is off.</summary>
        public int PoisonAfterConsecutiveFailures { get; }

        /// <summary>The non-pending status value a given-up, never-published document is stamped with. Never read while the poison arm is off.</summary>
        public string PoisonStatusValue { get; }

        /// <summary>The number of CONSECUTIVE failures of one document identity, at least one of which PUBLISHED, after which re-publishing stops. Always positive.</summary>
        public int GiveUpAfterUnconfirmedPublishes { get; }

        /// <summary>The non-pending status value a published-but-unconfirmed document is stamped with. Always reachable, so always validated.</summary>
        public string UnconfirmedStatusValue { get; }

        /// <summary>Whether the OPT-IN pre-publish poison arm is armed. A relay whose poison arm is off re-throws every pre-publish failure, as it always did.</summary>
        public bool IsPoisonEnabled => PoisonAfterConsecutiveFailures > 0;

        /// <summary>
        /// Whether ANY identity is mid-streak. A caller uses this to skip building a document identity for a SUCCESSFUL
        /// drain while nothing is tracked: with no slots there is nothing a success could clear, so an all-healthy relay
        /// pays no id read and no partition-key recovery.
        /// </summary>
        public bool HasTrackedFailures => !_failureStreaksByIdentity.IsEmpty;

        /// <summary>
        /// Records one FAILED drain of <paramref name="identity"/>, where <paramref name="messagePublished"/> reports
        /// whether that attempt got PAST its publish, and reports which bounded outcome — if any — the streak has now
        /// reached. Returns <see cref="GiveUpKind.None"/> while the streak is below the cap governing it and for a new
        /// identity arriving past <see cref="TrackedDocumentCapacity"/>.
        /// </summary>
        /// <remarks>
        /// The cap is chosen by the streak's STICKY published flag, and so is the outcome: a streak that EVER published is
        /// governed by <see cref="GiveUpAfterUnconfirmedPublishes"/> and can only terminate as
        /// <see cref="GiveUpKind.UnconfirmedPublish"/>; one that never did is governed by
        /// <see cref="PoisonAfterConsecutiveFailures"/> and can only terminate as <see cref="GiveUpKind.Poison"/>.
        /// </remarks>
        public GiveUpKind RecordFailure(OutboxDocumentIdentity identity, bool messagePublished)
        {
            if (string.IsNullOrEmpty(identity.DocumentId))
            {
                return GiveUpKind.None;
            }

            // The cap refuses only identities the policy is not ALREADY tracking, so a document mid-way to its threshold
            // still advances. Concurrent drains may admit a few identities beyond the cap between this read and the write
            // below; the overshoot is bounded by the number of drains in flight, which is what the cap exists to bound.
            if (!_failureStreaksByIdentity.ContainsKey(identity)
                && _failureStreaksByIdentity.Count >= TrackedDocumentCapacity)
            {
                return GiveUpKind.None;
            }

            FailureStreak streak = _failureStreaksByIdentity.AddOrUpdate(
                identity,
                new FailureStreak(ConsecutiveFailures: 1, MessagePublishedInStreak: messagePublished),
                (_, existing) => new FailureStreak(existing.ConsecutiveFailures + 1, existing.MessagePublishedInStreak || messagePublished));

            int governingCap = streak.MessagePublishedInStreak ? GiveUpAfterUnconfirmedPublishes : PoisonAfterConsecutiveFailures;
            if (governingCap <= 0 || streak.ConsecutiveFailures < governingCap)
            {
                return GiveUpKind.None;
            }

            _failureStreaksByIdentity.TryRemove(identity, out _);
            return streak.MessagePublishedInStreak ? GiveUpKind.UnconfirmedPublish : GiveUpKind.Poison;
        }

        /// <summary>
        /// Records one SUCCESSFUL drain of <paramref name="identity"/>, clearing its consecutive-failure count AND its
        /// sticky published flag so an intermittent failure can never accumulate across successful drains into a give-up,
        /// and so a fresh streak is classified by its OWN phases rather than an earlier streak's.
        /// </summary>
        public void RecordSuccess(OutboxDocumentIdentity identity)
        {
            if (string.IsNullOrEmpty(identity.DocumentId))
            {
                return;
            }

            _failureStreaksByIdentity.TryRemove(identity, out _);
        }

        /// <summary>
        /// The counted item: the Outbox Document's id TOGETHER with the logical partition it lives in — the SAME
        /// <c>(id, partition key)</c> pair <see cref="CosmosOutboxRelay.StampPoisonedAsync"/> patches. The id alone is
        /// under-qualified: two Outbox Documents sharing a MessageId in DIFFERENT partitions are different items, and
        /// counting them in one slot would give up on one after FEWER failures than the configured threshold.
        /// </summary>
        /// <remarks>
        /// A TYPED, COMPONENT-WISE-EQUALITY key (the <see cref="CosmosOutboxRelayHostedService.RelaySourceIdentityKey"/>
        /// precedent), never a delimiter-joined flat string: the id is compared ordinally and the partition key by its own
        /// value equality, so no byte inside either component can bleed across a component boundary — there is no boundary
        /// to abuse.
        /// </remarks>
        internal readonly record struct OutboxDocumentIdentity(string DocumentId, PartitionKey PartitionKey);

        /// <summary>
        /// One identity's run of consecutive failed drains: how many there have been, and whether ANY of them got past its
        /// publish. The flag is STICKY across the streak — that is what makes the alternating-phase escape unrepresentable
        /// and what keeps a published message from ever being labelled "never delivered".
        /// </summary>
        private readonly record struct FailureStreak(int ConsecutiveFailures, bool MessagePublishedInStreak);
    }
}
