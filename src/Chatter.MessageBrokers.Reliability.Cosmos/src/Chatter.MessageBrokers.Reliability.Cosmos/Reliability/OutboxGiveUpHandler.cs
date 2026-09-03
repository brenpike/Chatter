#nullable enable annotations
using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The ONE place a failed Outbox Relay drain is turned into a bounded outcome: it elects the give-up kind from the
    /// <see cref="OutboxGiveUpPolicy"/>, issues the matching stamp, counts it, and logs it. Both relay hosts call
    /// <see cref="TryGiveUpAsync"/> from their catch blocks, so the election, the stamp, the count and the log come from
    /// ONE derivation and two thin call sites rather than from two drifting copies.
    /// </summary>
    /// <remarks>
    /// ORDER IS LOAD-BEARING: stamp FIRST, so a failing stamp propagates before anything reports a give-up that never
    /// happened; then the matching counter; then the always-on Error log, since an application that opted into no meter
    /// would otherwise be left as silent as the storm this closes.
    /// </remarks>
    internal sealed class OutboxGiveUpHandler
    {
        private readonly OutboxGiveUpPolicy _policy;
        private readonly CosmosOutboxRelay _relay;
        private readonly ILogger? _logger;

        // logger is OPTIONAL, mirroring both hosts: a null logger is a documented silent no-op, because observability may
        // never be a construction prerequisite for the brake itself.
        internal OutboxGiveUpHandler(OutboxGiveUpPolicy policy, CosmosOutboxRelay relay, ILogger? logger)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _relay = relay ?? throw new ArgumentNullException(nameof(relay));
            _logger = logger;
        }

        /// <summary>
        /// Records one FAILED drain of <paramref name="document"/> and, when that failure elects a bounded outcome, gives
        /// up on the document: stamp, count, log. Returns whether the caller should SWALLOW the failure and continue the
        /// batch (true) or re-throw it unchanged (false).
        /// </summary>
        /// <remarks>
        /// ZERO COST WHEN NOTHING IS ARMED: a PRE-publish failure with the poison arm off returns before the document
        /// identity is built, so it pays no id read and no partition-key recovery, exactly as it did before either arm
        /// existed. A POST-publish failure always proceeds, because that arm has no off switch.
        /// </remarks>
        internal async Task<bool> TryGiveUpAsync(JsonElement document,
                                                 Container monitoredContainer,
                                                 IReadOnlyList<string> partitionKeyPath,
                                                 string leaseToken,
                                                 bool messagePublished,
                                                 Exception drainFailure,
                                                 CancellationToken cancellationToken)
        {
            if (!messagePublished && !_policy.IsPoisonEnabled)
            {
                return false;
            }

            OutboxGiveUpPolicy.OutboxDocumentIdentity identity = BuildDocumentIdentity(document, partitionKeyPath);
            GiveUpKind giveUpKind = _policy.RecordFailure(identity, messagePublished);
            if (giveUpKind == GiveUpKind.None)
            {
                return false;
            }

            if (giveUpKind == GiveUpKind.UnconfirmedPublish)
            {
                await GiveUpOnUnconfirmedPublishAsync(document, monitoredContainer, partitionKeyPath, identity.DocumentId, leaseToken, drainFailure, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await GiveUpAsPoisonedAsync(document, monitoredContainer, partitionKeyPath, identity.DocumentId, leaseToken, drainFailure, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        /// <summary>
        /// Records one SUCCESSFUL drain of <paramref name="document"/>, clearing its streak so an intermittent failure can
        /// never accumulate across successful drains into a give-up.
        /// </summary>
        /// <remarks>
        /// The identity is built only when SOMETHING is mid-streak: with no slots tracked there is nothing a success could
        /// clear, so an all-healthy relay pays no id read and no partition-key recovery on its happy path.
        /// </remarks>
        internal void RecordSuccessfulDrain(JsonElement document, IReadOnlyList<string> partitionKeyPath)
        {
            if (!_policy.HasTrackedFailures)
            {
                return;
            }

            _policy.RecordSuccess(BuildDocumentIdentity(document, partitionKeyPath));
        }

        // The opt-in #361 outcome. Nothing went out, so the honest record is "never delivered" and the honest recovery is
        // a re-drive.
        private async Task GiveUpAsPoisonedAsync(JsonElement document,
                                                 Container monitoredContainer,
                                                 IReadOnlyList<string> partitionKeyPath,
                                                 string documentId,
                                                 string leaseToken,
                                                 Exception drainFailure,
                                                 CancellationToken cancellationToken)
        {
            await _relay.StampPoisonedAsync(document, monitoredContainer, partitionKeyPath, cancellationToken).ConfigureAwait(false);

            CosmosReliabilityDiagnostics.RecordPoisonedDocument(leaseToken);

            _logger?.LogError(
                drainFailure,
                "The Cosmos Outbox Relay gave up on Outbox Document {DocumentId} on lease {LeaseToken} after {ConsecutiveFailures} consecutive failed drains ({GiveUpKind}); its message was NEVER published, so it is stamped '{GiveUpStatus}', is no longer published, and stays in the container for inspection. Re-driving it by patching its status back to '{PendingStatus}' once the defect is fixed is SAFE and is the intended recovery.",
                documentId,
                leaseToken,
                _policy.PoisonAfterConsecutiveFailures,
                nameof(GiveUpKind.Poison),
                _policy.PoisonStatusValue,
                CosmosOutboxDocument.StatusPending);
        }

        // The always-on outcome. The message ALREADY went out, so the record must not claim it was lost and the operator
        // must not be pointed at a re-drive, which would publish a duplicate.
        private async Task GiveUpOnUnconfirmedPublishAsync(JsonElement document,
                                                           Container monitoredContainer,
                                                           IReadOnlyList<string> partitionKeyPath,
                                                           string documentId,
                                                           string leaseToken,
                                                           Exception drainFailure,
                                                           CancellationToken cancellationToken)
        {
            await _relay.StampUnconfirmedAsync(document, monitoredContainer, partitionKeyPath, cancellationToken).ConfigureAwait(false);

            CosmosReliabilityDiagnostics.RecordUnconfirmedGiveUp(leaseToken);

            _logger?.LogError(
                drainFailure,
                "The Cosmos Outbox Relay stopped re-publishing Outbox Document {DocumentId} on lease {LeaseToken} after {ConsecutiveFailures} consecutive drains that PUBLISHED its message but could not confirm the delivered stamp ({GiveUpKind}); it is stamped '{GiveUpStatus}' and stays in the container for inspection. The message is NOT lost — it reached the broker at least once — so re-driving this document by patching its status back to '{PendingStatus}' would publish a DUPLICATE. Investigate the delivered stamp instead: the status patch path, the container's partition-key definition, or throttling.",
                documentId,
                leaseToken,
                _policy.GiveUpAfterUnconfirmedPublishes,
                nameof(GiveUpKind.UnconfirmedPublish),
                _policy.UnconfirmedStatusValue,
                CosmosOutboxDocument.StatusPending);
        }

        // Builds the give-up counter's key for one document.
        // INVARIANT: the key is the document's own id plus the partition key recovered by CosmosOutboxRelay's OWN
        // RecoverPartitionKey over the container's declared partition-key path — the SAME pair every give-up stamp and the
        // delivered stamp patch — so the counter and the patch come from ONE derivation and cannot diverge. When a
        // partition-key path segment is ABSENT from the document, that recovery yields a null component, so two such
        // documents collapse to ONE identity; that is CONSISTENT with the patch target by construction (both are this one
        // derivation), so it is correct rather than a new collapse.
        private static OutboxGiveUpPolicy.OutboxDocumentIdentity BuildDocumentIdentity(JsonElement document, IReadOnlyList<string> partitionKeyPath)
        {
            CosmosOutboxDocument.TryGetString(document, CosmosOutboxDocument.IdField, out string documentId);
            return new OutboxGiveUpPolicy.OutboxDocumentIdentity(documentId, CosmosOutboxRelay.RecoverPartitionKey(document, partitionKeyPath));
        }
    }
}
