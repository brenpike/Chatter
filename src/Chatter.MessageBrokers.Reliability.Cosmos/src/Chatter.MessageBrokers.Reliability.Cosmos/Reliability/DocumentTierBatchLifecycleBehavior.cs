using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The outermost document-tier pipeline behavior — the document-tier sibling of <c>UnitOfWorkBehavior</c>. It is
    /// PARTICIPATION-GATED via the <see cref="DocumentReliabilityRegistry"/>: only a command TYPE with a registration
    /// (the registry is a positive allowlist) opens a batch; every other command bare-passes-through to <c>next()</c>
    /// with no resolver call and no batch (ADR-0008). For a participant it resolves the partition key via the
    /// registration's Try/nullable resolver, derives+caches the registration's container via the
    /// <see cref="CosmosContainerFactory"/>, opens a <see cref="TransactionalBatch"/> on that container for the
    /// partition, exposes the doc-tier atomic-write handle on the document-tier surface, calls <c>next()</c>, then
    /// executes the batch once.
    /// </summary>
    /// <remarks>
    /// Three control paths: (1) non-participant — bare <c>next()</c>; (2) participant whose resolver returns a null
    /// partition key — bare <c>next()</c>, no batch opened; (3) participant with a partition — open batch, run the
    /// framework-owned lifecycle. The single batch-execute is GUARDED: an empty batch (zero staged ops) does NOT call
    /// the Cosmos transport, so unit tests need no live Cosmos. Must register OUTERMOST (first WithBehavior
    /// registration; the pipeline reverses behaviors so the first-registered is the outermost).
    /// </remarks>
    public sealed class DocumentTierBatchLifecycleBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
    {
        // INVARIANT: the inbox marker is staged FIRST (before next() and before any outbox/aggregate op), so it is
        // always batch op index 0. InspectBatchResponse reads the per-op result at this index to distinguish a
        // confirmed-duplicate marker 409 from any other failure.
        private const int MarkerOperationIndex = 0;

        private readonly DocumentReliabilityRegistry _registry;
        private readonly CosmosContainerFactory _containerFactory;
        private readonly DocumentTierReliabilitySurface _surface;

        public DocumentTierBatchLifecycleBehavior(DocumentReliabilityRegistry registry, CosmosContainerFactory containerFactory, DocumentTierReliabilitySurface surface)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _containerFactory = containerFactory ?? throw new ArgumentNullException(nameof(containerFactory));
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        }

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            // Participation = has a registration. A non-participant is a cheap registry miss and never reaches the
            // resolver or the container factory — no batch, no resolver call.
            if (!_registry.TryGet(typeof(TMessage), out DocumentReliabilityRegistration registration))
            {
                await next();
                return;
            }

            // A null inbound brokered message means this participant command is being handled OUTSIDE a broker-receive
            // context (GetInboundBrokeredMessage returns null for in-process commands). With no inbound message there
            // is no resolvable aggregate partition, so bare-pass-through BEFORE invoking the resolver. This makes the
            // null-inbound NRE class unrepresentable regardless of resolver implementation: although ResolvePartitionKey
            // is documented to tolerate a null inbound message, the framework no longer depends on every app resolver
            // honoring that contract — it is the same "no resolvable partition -> bare next()" semantics as the null-pk
            // path below, applied one step earlier.
            InboundBrokeredMessage inboundBrokeredMessage = messageHandlerContext.GetInboundBrokeredMessage();
            if (inboundBrokeredMessage is null)
            {
                await next();
                return;
            }

            // The resolver is only ever invoked for participants WITH an inbound message. It is Try/nullable: a null
            // partition key means "no resolvable partition for this message" — a legitimate no-op for a participant, so
            // no batch is opened.
            PartitionKey? partitionKey = registration.Resolver(inboundBrokeredMessage);
            if (partitionKey is null)
            {
                await next();
                return;
            }

            Container container = _containerFactory.GetDocumentContainer(registration);
            TransactionalBatch batch = container.CreateTransactionalBatch(partitionKey.Value);

            var handle = new CosmosAtomicWriteHandle(container, batch, partitionKey.Value, registration.PartitionKeyPath);
            _surface.CurrentHandle = handle;
            try
            {
                // Stamp the co-resident inbox dedup marker FIRST (before next() and before any outbox/aggregate op), so
                // it is deterministically batch op index 0. A 409-on-create of this marker at execute is the
                // confirmed-duplicate signal (closed-by-construction; no read-then-add, so no TOCTOU). When the message
                // has no identity (null/whitespace MessageId) no marker is stamped — we cannot dedup by identity — but
                // the batch is still opened and next() still runs so the aggregate/outbox path is unaffected (parity
                // with the relational ReceiveViaInbox null-id behavior + #238 null-safety).
                var markerStamped = TryStampInboxMarker(handle, inboundBrokeredMessage, registration.PartitionKeyPath);

                await next();

                // INVARIANT: the single batch-execute is the only commit point. Skip it when no op was staged so an
                // empty batch never calls the Cosmos transport. A marker-only batch has StagedOperationCount > 0, so it
                // executes — the marker MUST hit Cosmos to surface the 409 that confirms a duplicate.
                if (handle.StagedOperationCount > 0)
                {
                    TransactionalBatchResponse response = await batch.ExecuteAsync(messageHandlerContext?.CancellationToken ?? default);
                    InspectBatchResponse(response, markerStamped);
                }
            }
            finally
            {
                _surface.CurrentHandle = null;
            }
        }

        // Stamps the co-resident inbox dedup marker as the first op on the framework-owned batch when the inbound
        // message carries a usable identity. Returns true when a marker was staged (so InspectBatchResponse may
        // interpret a marker-op 409 as a confirmed duplicate), false when the message had no identity to dedup by.
        private static bool TryStampInboxMarker(CosmosAtomicWriteHandle handle, InboundBrokeredMessage inboundBrokeredMessage, IReadOnlyList<string> partitionKeyPath)
        {
            string messageId = inboundBrokeredMessage.MessageId;
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            CosmosInboxMarker marker = CosmosInboxMarker.From(messageId);
            IReadOnlyList<JsonElement> partitionKeyValues = CosmosPartitionKeyStamping.RecoverPartitionKeyValues(handle.PartitionKey, partitionKeyPath);
            var rendered = marker.ToJsonObject(partitionKeyPath, partitionKeyValues);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rendered, ChatterJson.Options);

            // INVARIANT: the marker authors a reserved inbox:-prefixed id, so it stages through the internal
            // reserved-write facet that BYPASSES the public reserved-namespace guard (the public StageCreateItemStream
            // would reject its own inbox: id). The behavior holds the concrete CosmosAtomicWriteHandle, which implements
            // the facet directly.
            ICosmosReservedWriteHandle reservedHandle = handle;
            reservedHandle.StageReservedCreateItemStream(new MemoryStream(bytes, writable: false));
            return true;
        }

        /// <summary>
        /// Inspects the <see cref="TransactionalBatchResponse"/> from the single batch-execute. The batch is
        /// all-or-nothing, so on success every write committed and the message is acked. On non-success NOTHING
        /// committed, and the three-way branch decides ack-vs-redeliver:
        /// <list type="bullet">
        /// <item>A marker was stamped this invocation AND the per-op result at <see cref="MarkerOperationIndex"/> is a
        /// 409 Conflict: CONFIRMED DUPLICATE. The marker already exists in this partition, so the message was handled
        /// before; the batch failed atomically (nothing re-committed) and the message is acked — return normally, NO
        /// throw, NO redeliver.</item>
        /// <item>Any other non-success (aggregate ETag/412, a 409 on a NON-marker op, a marker showing 424
        /// failed-dependency because another op failed, or no per-op results at all): throw
        /// <see cref="CosmosBatchExecutionException"/> so the transport redelivers/retries.</item>
        /// </list>
        /// Only the MARKER op's 409 is swallowed — a batch-level 409 alone, or an aggregate-op 409, must throw, so a
        /// genuine concurrency/conflict failure is never mistaken for a duplicate (conflating the two = silent message
        /// loss).
        /// </summary>
        private static void InspectBatchResponse(TransactionalBatchResponse response, bool markerStamped)
        {
            // INVARIANT: a forced aggregate ETag/412 (or any non-success op) surfaces as IsSuccessStatusCode == false on
            // the batch; throwing here is what prevents an ack when the writes did not commit.
            if (response is not null && response.IsSuccessStatusCode)
            {
                return;
            }

            // No response, or a non-success response with no per-op results to inspect: never swallow — throw so the
            // message redelivers. The duplicate decision REQUIRES the marker op's own 409; a batch-level status alone
            // is not sufficient.
            if (response is null || response.Count <= MarkerOperationIndex)
            {
                throw new CosmosBatchExecutionException(response);
            }

            // Confirmed duplicate ONLY when we stamped a marker this invocation AND the marker op (index 0) itself is a
            // 409 Conflict. Any other non-success — including a 409 on a non-marker op or a marker 424 while another op
            // failed — falls through to the throw below (redeliver/retry). This swallow is SOUND because the inbox:
            // id prefix is an ENFORCED Chatter-reserved id namespace (CosmosItemId.GuardNotReserved rejects reserved-
            // prefix ids on the public atomic-write surface, parity with the trusted _chatterType reserved field) — NOT
            // a naming convention — so a 409 on the framework's marker create can ONLY mean a prior Chatter marker, never
            // a colliding app document authored with an inbox: id (which can no longer be staged).
            TransactionalBatchOperationResult markerResult = response[MarkerOperationIndex];
            if (markerStamped && markerResult is not null && markerResult.StatusCode == HttpStatusCode.Conflict)
            {
                return;
            }

            throw new CosmosBatchExecutionException(response);
        }
    }
}
