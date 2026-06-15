using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Routing.Options;
using Microsoft.Azure.Cosmos;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // The aggregate document the test handlers upsert. A plain POCO shape with an id, the partition-key value at the
    // declared container PK path ("pk"), and a payload field. Built as a System.Text.Json object so the test owns the
    // wire shape (no Cosmos-SDK serialization) and the id/pk are stamped exactly where the container expects them.
    public static class AggregateDocument
    {
        public const string IdField = "id";
        public const string PartitionField = "pk";
        public const string PayloadField = "payload";

        public static Stream ToStream(string id, string partition, string payload)
        {
            var document = new JsonObject
            {
                [IdField] = id,
                [PartitionField] = partition,
                [PayloadField] = payload,
            };

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            return new MemoryStream(bytes, writable: false);
        }
    }

    // The participant command bound to the primary document container. Carries the aggregate id, the partition value,
    // a payload (sent to the outbox as a follow-up), and the outbound destination.
    public sealed class PrimaryParticipantCommand : ICommand
    {
        public string AggregateId { get; set; }
        public string Partition { get; set; }
        public string Payload { get; set; }
        public string OutboundDestination { get; set; }

        // A deterministic message id for the follow-up Send so the co-resident outbox doc's id is
        // outbox:{encoded(OutboundMessageId)} and the test can read it back by a known key.
        public string OutboundMessageId { get; set; }
    }

    // A second participant command bound to a SECOND container (multi-container atomicity). Same shape.
    public sealed class SecondaryParticipantCommand : ICommand
    {
        public string AggregateId { get; set; }
        public string Partition { get; set; }
        public string Payload { get; set; }
        public string OutboundDestination { get; set; }
        public string OutboundMessageId { get; set; }
    }

    // A non-participant command: it has NO document-reliability registration, so the document-tier behavior bare-passes
    // it through and its handler's Send routes broker-direct to the capturing sink (no batch, no inbox/outbox doc).
    public sealed class NonParticipantCommand : ICommand
    {
        public string Payload { get; set; }
        public string OutboundDestination { get; set; }
        public string OutboundMessageId { get; set; }
    }

    // The partition-key resolvers. The inbound message carries the partition value as an application property; the
    // resolver reads it and builds a single-segment PartitionKey. A null inbound message (in-process command) yields
    // null (no partition) — the idiomatic "no resolvable partition" answer.
    public static class TestResolvers
    {
        // The application property the partition value travels on (set by the harness when delivering).
        public const string PartitionProperty = "test.partition";

        public static PartitionKey? ResolvePartition(InboundBrokeredMessage inboundBrokeredMessage)
        {
            if (inboundBrokeredMessage is null)
            {
                return null;
            }

            if (inboundBrokeredMessage.MessageContext.TryGetValue(PartitionProperty, out object value) && value is string partition && !string.IsNullOrWhiteSpace(partition))
            {
                return new PartitionKey(partition);
            }

            return null;
        }
    }

    // The success handler for a participant command: it stages an aggregate upsert AND a follow-up Send through Chatter
    // so the co-resident outbox doc rides the SAME framework-owned batch. The aggregate is staged through the active
    // ICosmosAtomicWriteHandle (NOT the raw container) so it joins the framework batch and the atomicity assertion is
    // meaningful. The follow-up Send is the outbox contribution (CosmosBrokeredMessageOutbox stages the outbox doc on
    // the same handle).
    public sealed class PrimaryParticipantHandler : IMessageHandler<PrimaryParticipantCommand>
    {
        private readonly IDocumentTierReliabilitySurface _surface;

        public PrimaryParticipantHandler(IDocumentTierReliabilitySurface surface)
            => _surface = surface ?? throw new ArgumentNullException(nameof(surface));

        public Task Handle(PrimaryParticipantCommand message, IMessageHandlerContext context)
            => StageAggregateAndOutbox(_surface, message.AggregateId, message.Partition, message.Payload, message.OutboundDestination, message.OutboundMessageId, context);

        // Stages the aggregate create on the active handle (so it joins the framework batch) and sends a follow-up
        // command through the broker context (so the Cosmos outbox stages the co-resident outbox doc on the same
        // batch). The follow-up's MessageId is set explicitly so the outbox doc's id is deterministic. Shared by both
        // participant handlers.
        internal static async Task StageAggregateAndOutbox(IDocumentTierReliabilitySurface surface, string aggregateId, string partition, string payload, string outboundDestination, string outboundMessageId, IMessageHandlerContext context)
        {
            ICosmosAtomicWriteHandle handle = surface.CurrentHandle
                ?? throw new InvalidOperationException("No active document-tier handle; the participant must be delivered through the receive pipeline.");

            handle.StageCreateItemStream(AggregateDocument.ToStream(aggregateId, partition, payload));

            // The follow-up Send rides the same batch via the Cosmos outbox. A fresh command targeted at the capturing
            // sink's destination with a deterministic MessageId; the relay republishes it once committed.
            await context.Send(new OutboundFollowUp { Payload = payload }, outboundDestination, new SendOptions { MessageId = outboundMessageId });
        }
    }

    // The secondary participant handler: identical behavior, bound to the second container via its own registration.
    public sealed class SecondaryParticipantHandler : IMessageHandler<SecondaryParticipantCommand>
    {
        private readonly IDocumentTierReliabilitySurface _surface;

        public SecondaryParticipantHandler(IDocumentTierReliabilitySurface surface)
            => _surface = surface ?? throw new ArgumentNullException(nameof(surface));

        public Task Handle(SecondaryParticipantCommand message, IMessageHandlerContext context)
            => PrimaryParticipantHandler.StageAggregateAndOutbox(_surface, message.AggregateId, message.Partition, message.Payload, message.OutboundDestination, message.OutboundMessageId, context);
    }

    // The conflict handler: it REPLACES the (pre-seeded) aggregate with a STALE IfMatchEtag so the framework
    // batch-execute fails with a 412 (precondition failed). Because the batch is all-or-nothing, NEITHER the aggregate
    // mutation NOR the co-resident outbox doc commit. The test pre-seeds the aggregate so the replace targets an
    // existing item whose actual ETag differs from the stale one supplied (a clean 412 rather than a 404). Used to
    // prove forced-concurrency-failure atomicity through the public receive path.
    public sealed class ConflictParticipantHandler : IMessageHandler<PrimaryParticipantCommand>
    {
        // A deterministic, definitely-stale ETag: no live item carries this ETag, so an IfMatch replace against it fails
        // the precondition (412).
        private const string StaleETag = "\"00000000-0000-0000-0000-000000000000\"";

        private readonly IDocumentTierReliabilitySurface _surface;

        public ConflictParticipantHandler(IDocumentTierReliabilitySurface surface)
            => _surface = surface ?? throw new ArgumentNullException(nameof(surface));

        public async Task Handle(PrimaryParticipantCommand message, IMessageHandlerContext context)
        {
            ICosmosAtomicWriteHandle handle = _surface.CurrentHandle
                ?? throw new InvalidOperationException("No active document-tier handle; the participant must be delivered through the receive pipeline.");

            // Stage a replace of the pre-seeded aggregate with a stale IfMatchEtag so the batch fails the precondition at
            // execute time (412). The whole batch — including the co-resident outbox doc staged by the follow-up Send
            // below — rolls back together.
            var requestOptions = new TransactionalBatchItemRequestOptions { IfMatchEtag = StaleETag };
            var aggregate = new Dictionary<string, object>
            {
                [AggregateDocument.IdField] = message.AggregateId,
                [AggregateDocument.PartitionField] = message.Partition,
                [AggregateDocument.PayloadField] = message.Payload,
            };
            handle.StageReplaceItem(message.AggregateId, aggregate, requestOptions);

            await context.Send(new OutboundFollowUp { Payload = message.Payload }, message.OutboundDestination, new SendOptions { MessageId = message.OutboundMessageId });
        }
    }

    // The handler for the non-participant command: there is no document-tier batch, so its Send routes broker-direct to
    // the capturing sink (proving the non-participant-bypass arm). It does NOT touch the surface handle (there is none).
    public sealed class NonParticipantHandler : IMessageHandler<NonParticipantCommand>
    {
        public Task Handle(NonParticipantCommand message, IMessageHandlerContext context)
            => context.Send(new OutboundFollowUp { Payload = message.Payload }, message.OutboundDestination, new SendOptions { MessageId = message.OutboundMessageId });
    }

    // The follow-up event/command the handlers Send; it becomes the outbox doc (participant) or a broker-direct publish
    // (non-participant). A command so it routes through Send.
    public sealed class OutboundFollowUp : ICommand
    {
        public string Payload { get; set; }
    }
}
