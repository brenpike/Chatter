using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The standalone, lease-less Cosmos inbox-dedup gate (#253, ADR-0009). Wired through the existing
    /// <c>InboxBehavior&lt;T&gt;</c> seam, <see cref="ReceiveViaInbox"/> performs an anti-TOCTOU write-ahead claim: it
    /// <c>CreateItemStream</c>s a <see cref="CosmosInboxMarker"/> for the inbound message id on an
    /// <c>/idempotencyKey</c>-partitioned container BEFORE running the handler, so a create-409 marks a candidate
    /// duplicate closed-by-construction (no read-then-add). Unlike the document tier
    /// (<see cref="DocumentTierBatchLifecycleBehavior{TMessage}"/>) it opens no <c>TransactionalBatch</c>, registers no
    /// lease/relay/outbox, and stands alone as the once-only gate for a stateless consumer.
    /// </summary>
    /// <remarks>
    /// SOUNDNESS (ADR-0009 D1, confirm-not-infer + two-phase claim→complete). The claim is TWO-PHASE (ADR-0009 D1
    /// amendment): phase 1 <c>CreateItemStream</c>s a PENDING marker (<c>Completed=false</c>) before the handler; a fresh
    /// 201 runs the handler, then phase 2 <c>PatchItemStream</c>s the marker to <c>Completed=true</c>. A redelivery
    /// confirms a duplicate on COMPLETION, not mere existence — this closes the abandoned-marker permanent-loss defect
    /// (a marker persisted but abandoned by a hard-kill between the 201 and handler completion, which best-effort
    /// compensation cannot delete because the <c>catch</c> never runs on a SIGKILL). A create-409 is NOT trusted as a bare
    /// duplicate: the application owns the container (it registers the <see cref="CosmosClient"/> the container is derived
    /// from) and can author a colliding <c>inbox:</c>-prefixed id through a non-staging path no guard closes, so inferring
    /// "duplicate" from the bare 409 would silently lose the colliding message's first delivery. On a 409 the inbox
    /// point-reads the conflicting document (confirm-not-infer: <c>_chatterType == "inbox"</c> AND <c>MessageId</c> equals
    /// this id, checked BEFORE inspecting completion) and resolves THREE ways: a genuine COMPLETED marker for this id is a
    /// confirmed duplicate → SKIP; a genuine but PENDING/abandoned marker → TAKE OVER (run the handler, then complete); a
    /// non-marker / different-id / non-success / 404-exhausted read → REDELIVER (throws). A not-yet-visible 404 retries
    /// within a bounded budget. The read is cold-path-only (the 409 branch), gates no write, and is therefore not a TOCTOU.
    /// <para>
    /// SIDE-EFFECT TIMING / handler-idempotency contract. The claim is write-ahead, so on a handler failure the marker is
    /// best-effort compensation-deleted and the ORIGINAL exception rethrown for redelivery; on handler SUCCESS a phase-2
    /// completion-write FAILURE THROWS (redeliver) rather than acking with a pending marker. Non-batched handler side
    /// effects (external HTTP, non-Cosmos writes) that ran before a failure re-run on redelivery (AT-LEAST-ONCE), a
    /// pending-marker take-over re-runs the handler (AT-LEAST-ONCE), and a failed compensation-delete after a partial
    /// handler is a documented edge. Handlers behind this inbox MUST be idempotent AND concurrency-safe (safe under
    /// concurrent execution of the same id).
    /// </para>
    /// <para>
    /// CONCURRENT TAKE-OVER (accepted, ADR-0009 D1 sub-note). Take-over adopts a PENDING marker whether it is abandoned (a
    /// hard-kill between the 201 and completion) OR still LIVE — written by a genuinely-concurrent in-flight delivery of
    /// the same id whose handler has not yet completed — because the lease-less design cannot distinguish "abandoned" from
    /// "live-in-flight" without the liveness lease it rejects. Two concurrent in-flight deliveries of the same id therefore
    /// both run the handler, CONCURRENTLY. This gate DEDUPS REDELIVERIES; it is NOT a distributed lock and does NOT
    /// serialize concurrent delivery — mutual exclusion for concurrent delivery is the TRANSPORT's message-lock / session
    /// (e.g. Azure Service Bus PeekLock or a session), not this dedup gate. Hence the contract is concurrency-safe, not
    /// merely sequential-retry-safe.
    /// </para>
    /// </remarks>
    public sealed class CosmosBrokeredMessageInbox : IBrokeredMessageInbox, IInboxDeduplicator
    {
        private readonly Container _container;
        private readonly IReadOnlyList<string> _partitionKeyPath;
        private readonly int? _markerTimeToLive;
        private readonly int _readBackMaxAttempts;
        private readonly Func<int, TimeSpan> _readBackBackoff;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        /// <summary>
        /// Production constructor: derives the confirm read-back's constant backoff from <paramref name="readBackInterval"/>
        /// and delays via <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
        /// </summary>
        public CosmosBrokeredMessageInbox(Container container,
                                          IReadOnlyList<string> partitionKeyPath,
                                          int? markerTimeToLive,
                                          int readBackMaxAttempts,
                                          TimeSpan readBackInterval)
            : this(container, partitionKeyPath, markerTimeToLive, readBackMaxAttempts, _ => readBackInterval, (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
        {
        }

        // Internal test-seam constructor (ADR-0009 D7): injects the read-back backoff schedule and the delay primitive so
        // the bounded confirm loop runs at zero wall-clock under unit test. The production constructor supplies the real
        // constant backoff and Task.Delay.
        internal CosmosBrokeredMessageInbox(Container container,
                                            IReadOnlyList<string> partitionKeyPath,
                                            int? markerTimeToLive,
                                            int readBackMaxAttempts,
                                            Func<int, TimeSpan> readBackBackoff,
                                            Func<TimeSpan, CancellationToken, Task> delay)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _partitionKeyPath = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));
            _markerTimeToLive = markerTimeToLive;
            _readBackMaxAttempts = readBackMaxAttempts;
            _readBackBackoff = readBackBackoff ?? throw new ArgumentNullException(nameof(readBackBackoff));
            _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        }

        public async Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> messageReceiver)
        {
            _ = messageBrokerContext ?? throw new ArgumentNullException(nameof(messageBrokerContext));
            _ = messageReceiver ?? throw new ArgumentNullException(nameof(messageReceiver));

            // Same identity the in-memory inbox reads. A null/whitespace id FAILS LOUD (ADR-0009 D2): the handler never
            // runs and nothing is written, matching the document tier and the in-memory inbox rather than the EF
            // run-handler-with-no-dedup stance — silently running with no dedup defeats the once-only guarantee.
            string messageId = messageBrokerContext.BrokeredMessage.MessageId;
            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new InvalidOperationException(
                    "A brokered message must carry a non-null, non-whitespace message id for the Cosmos inbox to claim it. " +
                    "The handler was not run and no marker was written; set a message id upstream (a raw Azure Service Bus " +
                    "producer must set one) or accept this loud failure.");
            }

            CancellationToken cancellationToken = messageBrokerContext.CancellationToken;
            var partitionKey = new PartitionKey(messageId);
            // Phase 1 stamps a PENDING claim (Completed=false), so a genuine standalone marker ALWAYS carries the
            // completion field and a redelivery confirms a duplicate on COMPLETION, not mere existence (ADR-0009 D1
            // amendment). This closes the abandoned-marker permanent-loss defect the single-phase confirm-on-existence had.
            CosmosInboxMarker marker = CosmosInboxMarker.From(messageId, _markerTimeToLive, completed: false);
            using Stream payload = BuildMarkerStream(marker, partitionKey);

            // Write-ahead claim. CreateItemStreamAsync returns a NON-throwing ResponseMessage — branch on the status code
            // rather than catching CosmosException for the 409.
            using (ResponseMessage create = await _container.CreateItemStreamAsync(payload, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (create.StatusCode == HttpStatusCode.Conflict)
                {
                    // THREE-WAY confirm (ADR-0009 D1 amendment). A genuine COMPLETED marker for this id is a confirmed
                    // duplicate -> skip. A genuine but PENDING/abandoned marker -> fall through and TAKE OVER (run the
                    // handler, then complete). A non-confirmable / non-marker / 404-exhausted read throws (redeliver).
                    bool alreadyCompleted = await ConfirmDuplicateCompletion(marker.Id, messageId, partitionKey, cancellationToken).ConfigureAwait(false);
                    if (alreadyCompleted)
                    {
                        return;
                    }
                }
                else
                {
                    // Any other non-success (429/503/500/…) cannot claim the message, so REDELIVER (throw) rather than run
                    // the handler; EnsureSuccessStatusCode surfaces the CosmosException for the observed status.
                    create.EnsureSuccessStatusCode();
                }
            }

            // A fresh 201 claim OR a pending-marker take-over converges here: run the handler, then Phase 2 completes the
            // claim. Both cases are identical from here — a fresh claim wrote its own pending marker; a take-over adopts
            // an abandoned pending marker written by an earlier delivery.
            await RunHandlerThenComplete(messageReceiver, marker.Id, partitionKey, cancellationToken).ConfigureAwait(false);
        }

        // Runs the handler for a fresh claim or a pending-marker take-over, then Phase 2 flips the claim to completed. On a
        // handler EXCEPTION the write-ahead marker is best-effort compensation-deleted and the ORIGINAL exception rethrown
        // for redelivery; the completion write is NOT reached. On handler SUCCESS the claim is completed — a
        // completion-write FAILURE THROWS (redeliver), never swallowed (ADR-0009 D1 amendment).
        private async Task RunHandlerThenComplete(Func<Task> messageReceiver, string markerId, PartitionKey partitionKey, CancellationToken cancellationToken)
        {
            try
            {
                await messageReceiver().ConfigureAwait(false);
            }
            catch
            {
                // Compensation MUST NOT reuse the receive cancellation token: the handler commonly fails BECAUSE that
                // token was canceled (graceful shutdown), and DeleteItemStreamAsync would then throw on an already-canceled
                // token before ever issuing the delete — turning the swallowed best-effort compensation into a GUARANTEED
                // no-op exactly when it is needed, stranding the write-ahead marker so redelivery confirms it and silently
                // skips the handler (message loss). Use an independent cleanup token so the delete is actually attempted;
                // the Cosmos SDK's own request timeout bounds it. Compensation stays best-effort (failures swallowed).
                await BestEffortCompensationDelete(markerId, partitionKey, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await CompleteClaim(markerId, partitionKey, cancellationToken).ConfigureAwait(false);
        }

        // PHASE 2 (ADR-0009 D1 amendment): flip the write-ahead claim from pending to Completed=true via a single
        // PatchItemStream set-op. Confirm-on-COMPLETION (a redelivery skips ONLY a completed marker) requires this write
        // to actually land, so a completion-write FAILURE THROWS (EnsureSuccessStatusCode surfaces the CosmosException)
        // and the message REDELIVERS rather than acking with a still-pending marker — never swallowed. PatchItemStream
        // (not ReplaceItem) so only the completion field is touched.
        private async Task CompleteClaim(string markerId, PartitionKey partitionKey, CancellationToken cancellationToken)
        {
            var completion = new[] { PatchOperation.Set("/" + CosmosInboxMarker.CompletedField, true) };
            using ResponseMessage patch = await _container.PatchItemStreamAsync(markerId, partitionKey, completion, requestOptions: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            patch.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Not supported on the standalone Cosmos inbox. Always throws <see cref="NotSupportedException"/>: this inbox
        /// dedups via the write-ahead <c>CreateItemStream</c> claim in <see cref="ReceiveViaInbox"/> plus a
        /// confirm-on-409 (closed-by-construction, no read-then-add), NOT via a <see cref="HasBeenReceived"/> read. A
        /// <see cref="HasBeenReceived"/> read would be exactly the TOCTOU the write-ahead claim eliminates, so it is
        /// unsupported — same stance as the document tier's <see cref="CosmosInboxDeduplicator"/>.
        /// </summary>
        public Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "The standalone Cosmos inbox does not dedup via HasBeenReceived. It performs a write-ahead CreateItemStream " +
                "claim of an inbox marker before the handler runs; a 409-on-create is the candidate-duplicate signal, " +
                "confirmed by point-reading the conflicting marker (closed-by-construction, no read-then-add/TOCTOU). A " +
                "HasBeenReceived read would reintroduce the TOCTOU this design eliminates, so it is unsupported.");

        // Renders the marker body with the message-id partition value stamped at the container's single-segment
        // partition-key path, matching the outbox/document-tier carriage contract.
        private Stream BuildMarkerStream(CosmosInboxMarker marker, PartitionKey partitionKey)
        {
            IReadOnlyList<JsonElement> partitionKeyValues = CosmosPartitionKeyStamping.RecoverPartitionKeyValues(partitionKey, _partitionKeyPath);
            JsonObject document = marker.ToJsonObject(_partitionKeyPath, partitionKeyValues, _markerTimeToLive);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, ChatterJson.Options);
            return new MemoryStream(bytes, writable: false);
        }

        // CONFIRM (ADR-0009 D1 amendment): point-read the conflicting marker and decide among THREE outcomes. Returns true
        // when it is a genuine Chatter inbox marker for this id that is COMPLETED (confirmed duplicate -> caller skips the
        // handler). Returns false when it is a genuine but NOT-yet-completed (pending/abandoned) marker for this id (caller
        // TAKES OVER: runs the handler, then completes). THROWS when the conflict is non-confirmable — a non-marker /
        // different-id doc, a non-success read, or a 404 whose read-back budget is exhausted — so the message REDELIVERS
        // rather than silently skipping. A not-yet-visible 404 retries within the bounded budget with backoff.
        private async Task<bool> ConfirmDuplicateCompletion(string markerId, string messageId, PartitionKey partitionKey, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < _readBackMaxAttempts; attempt++)
            {
                using ResponseMessage read = await _container.ReadItemStreamAsync(markerId, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (read.StatusCode == HttpStatusCode.NotFound)
                {
                    // The conflicting marker is not yet visible under session consistency (or a TTL/delete race removed
                    // it). Retry within budget; once the budget is exhausted the duplicate is non-confirmable -> redeliver.
                    if (attempt + 1 < _readBackMaxAttempts)
                    {
                        await _delay(_readBackBackoff(attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    break;
                }

                // A successful read is classified by inspecting the conflicting doc: a genuine COMPLETED marker for this id
                // is a confirmed duplicate (skip); a genuine but pending/abandoned marker is taken over (run the handler,
                // then complete); anything else (non-marker / different id) is an app-authored collision that must
                // redeliver. A non-success read (429/503/…) cannot confirm -> redeliver.
                if (read.IsSuccessStatusCode)
                {
                    ConflictingMarkerState state = InspectConflictingMarker(read.Content, messageId);
                    if (state == ConflictingMarkerState.Completed)
                    {
                        return true;
                    }

                    if (state == ConflictingMarkerState.Pending)
                    {
                        return false;
                    }
                }

                break;
            }

            throw new InvalidOperationException(
                $"The Cosmos inbox observed a create-conflict for message id '{messageId}' but could not confirm the " +
                "conflicting document is a genuine Chatter inbox marker for that id within the read-back budget. Redelivering " +
                "rather than silently skipping the handler (an unconfirmed 409 could be an app-authored id collision whose " +
                "first delivery would otherwise be lost).");
        }

        // The three ways a conflicting document classifies on the 409 branch (ADR-0009 D1 amendment).
        private enum ConflictingMarkerState
        {
            // Not a genuine Chatter inbox marker for the expected id (non-marker / different id / unparseable) -> redeliver.
            NotConfirmable,

            // A genuine inbox marker for this id that is NOT yet completed (pending or abandoned) -> take over.
            Pending,

            // A genuine inbox marker for this id whose Completed field is boolean true -> confirmed duplicate -> skip.
            Completed,
        }

        // Best-effort compensation after a handler failure on a fresh claim: swallow ANY delete failure (a thrown
        // exception OR a non-success ResponseMessage) so the ORIGINAL handler exception is the one that propagates and
        // drives redelivery. A failed compensation-delete after a partial handler is a documented edge (ADR-0009).
        private async Task BestEffortCompensationDelete(string markerId, PartitionKey partitionKey, CancellationToken cancellationToken)
        {
            try
            {
                using ResponseMessage delete = await _container.DeleteItemStreamAsync(markerId, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // INVARIANT: compensation is best-effort — never let a delete failure mask the original handler exception.
            }
        }

        // Parses the conflicting document ONCE and classifies it (ADR-0009 D1 amendment). Confirm-not-infer runs FIRST:
        // the JSON root must be an object whose discriminator equals the inbox kind AND whose MessageId equals
        // expectedMessageId (ordinal); only THEN is completion inspected. A Completed field equal to boolean true is
        // Completed; any other genuine-marker shape (Completed=false, absent, or non-boolean) is Pending, so a
        // persisted-but-abandoned marker — including a pre-amendment single-phase marker with no Completed field — is
        // taken over rather than confirming a false duplicate. An empty, unparseable, non-object, or non-marker payload is
        // NotConfirmable (the caller redelivers). Re-implements the document tier's confirm shape
        // (DocumentTierBatchLifecycleBehavior.IsConfirmedInboxMarker) — a private helper this cannot call.
        private static ConflictingMarkerState InspectConflictingMarker(Stream content, string expectedMessageId)
        {
            if (content is null)
            {
                return ConflictingMarkerState.NotConfirmable;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return ConflictingMarkerState.NotConfirmable;
                }

                if (!document.RootElement.TryGetProperty(CosmosOutboxDocument.DiscriminatorField, out JsonElement discriminator)
                    || discriminator.ValueKind != JsonValueKind.String
                    || !string.Equals(discriminator.GetString(), CosmosItemId.InboxKind, StringComparison.Ordinal))
                {
                    return ConflictingMarkerState.NotConfirmable;
                }

                if (!document.RootElement.TryGetProperty(CosmosInboxMarker.MessageIdField, out JsonElement markerMessageId)
                    || markerMessageId.ValueKind != JsonValueKind.String
                    || !string.Equals(markerMessageId.GetString(), expectedMessageId, StringComparison.Ordinal))
                {
                    return ConflictingMarkerState.NotConfirmable;
                }

                // Genuine marker for this id. Confirm-on-COMPLETION: only a Completed==true marker is a duplicate.
                if (document.RootElement.TryGetProperty(CosmosInboxMarker.CompletedField, out JsonElement completed)
                    && completed.ValueKind == JsonValueKind.True)
                {
                    return ConflictingMarkerState.Completed;
                }

                return ConflictingMarkerState.Pending;
            }
            catch (JsonException)
            {
                return ConflictingMarkerState.NotConfirmable;
            }
        }
    }
}
