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
    /// SOUNDNESS (ADR-0009 D1, confirm-not-infer). A create-409 is NOT trusted as a bare duplicate: the application owns
    /// the container (it registers the <see cref="CosmosClient"/> the container is derived from) and can author a
    /// colliding <c>inbox:</c>-prefixed id through a non-staging path no guard closes, so inferring "duplicate" from the
    /// bare 409 would silently lose the colliding message's first delivery. On a 409 the inbox point-reads the conflicting
    /// document and skips the handler ONLY when it is a genuine Chatter inbox marker for THIS message id; a not-yet-visible
    /// 404 retries within a bounded budget, and an exhausted or non-confirmable read REDELIVERS (throws) rather than
    /// silently skipping. The read is cold-path-only (the 409 branch), gates no write, and is therefore not a TOCTOU.
    /// <para>
    /// SIDE-EFFECT TIMING / handler-idempotency contract. The claim is write-ahead, so on a handler failure the marker is
    /// best-effort compensation-deleted and the ORIGINAL exception rethrown for redelivery; non-batched handler side
    /// effects (external HTTP, non-Cosmos writes) that ran before the failure re-run on redelivery (AT-LEAST-ONCE), and a
    /// failed compensation-delete after a partial handler is a documented edge. Handlers behind this inbox MUST be idempotent.
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
            CosmosInboxMarker marker = CosmosInboxMarker.From(messageId, _markerTimeToLive);
            using Stream payload = BuildMarkerStream(marker, partitionKey);

            // Write-ahead claim. CreateItemStreamAsync returns a NON-throwing ResponseMessage — branch on the status code
            // rather than catching CosmosException for the 409.
            using (ResponseMessage create = await _container.CreateItemStreamAsync(payload, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (create.StatusCode == HttpStatusCode.Conflict)
                {
                    await ConfirmDuplicateOrRedeliver(marker.Id, messageId, partitionKey, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // Any other non-success (429/503/500/…) cannot claim the message, so REDELIVER (throw) rather than run
                // the handler; EnsureSuccessStatusCode surfaces the CosmosException for the observed status.
                create.EnsureSuccessStatusCode();
            }

            // Fresh claim (201). Run the handler; on failure best-effort compensation-delete the marker so a redelivery
            // can re-claim, then ALWAYS rethrow the original exception.
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
                await BestEffortCompensationDelete(marker.Id, partitionKey, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
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

        // CONFIRM (ADR-0009 D1): point-read the conflicting marker and RETURN (caller skips the handler) only when it is a
        // genuine Chatter inbox marker for this message id. A not-yet-visible 404 retries within the bounded budget with
        // backoff; an exhausted budget, a non-success read, or a non-confirmable payload (non-marker / different
        // MessageId) THROWS so the message is redelivered rather than silently skipped.
        private async Task ConfirmDuplicateOrRedeliver(string markerId, string messageId, PartitionKey partitionKey, CancellationToken cancellationToken)
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

                // A non-success read (429/503/…) cannot confirm -> redeliver. A successful read confirms ONLY when the
                // conflicting doc is a genuine inbox marker for this id; any other success (non-marker / different id) is
                // an app-authored collision that must redeliver, never skip.
                if (read.IsSuccessStatusCode && IsConfirmedInboxMarker(read.Content, messageId))
                {
                    return;
                }

                break;
            }

            throw new InvalidOperationException(
                $"The Cosmos inbox observed a create-conflict for message id '{messageId}' but could not confirm the " +
                "conflicting document is a genuine Chatter inbox marker for that id within the read-back budget. Redelivering " +
                "rather than silently skipping the handler (an unconfirmed 409 could be an app-authored id collision whose " +
                "first delivery would otherwise be lost).");
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

        // Confirms the conflicting document is a genuine Chatter inbox marker for the expected message id: the JSON root
        // must be an object whose discriminator equals the inbox kind AND whose MessageId equals expectedMessageId
        // (ordinal). An empty, unparseable, non-object, or field-missing payload is NOT confirmed (returns false) so the
        // caller redelivers rather than swallowing. Re-implements the document tier's confirm shape
        // (DocumentTierBatchLifecycleBehavior.IsConfirmedInboxMarker) — a private helper this cannot call.
        private static bool IsConfirmedInboxMarker(Stream content, string expectedMessageId)
        {
            if (content is null)
            {
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (!document.RootElement.TryGetProperty(CosmosOutboxDocument.DiscriminatorField, out JsonElement discriminator)
                    || discriminator.ValueKind != JsonValueKind.String
                    || !string.Equals(discriminator.GetString(), CosmosItemId.InboxKind, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!document.RootElement.TryGetProperty(CosmosInboxMarker.MessageIdField, out JsonElement markerMessageId)
                    || markerMessageId.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                return string.Equals(markerMessageId.GetString(), expectedMessageId, StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
