using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutbox
{
    public class WhenSendingToOutbox : Testing.Core.Context
    {
        private static OutboundBrokeredMessage Message(string messageId = "msg/with?reserved#chars")
        {
            var context = new Dictionary<string, object> { ["custom-header"] = "value" };
            return new OutboundBrokeredMessage(messageId, new byte[] { 1, 2, 3 }, context, "destination-queue", new JsonBodyConverter());
        }

        private static IEnumerable<OutboundBrokeredMessage> Messages(int count)
        {
            for (var i = 0; i < count; i++)
            {
                yield return Message($"msg-{i}");
            }
        }

        // Builds the real internal handle over a batch mock that captures each staged CreateItemStream payload, then
        // exposes the handle on the surface so the outbox contributes to the same framework-owned batch.
        private static (IBrokeredMessageOutbox outbox, DocumentTierReliabilitySurface surface, List<Stream> staged, Mock<TransactionalBatch> batch)
            Harness(PartitionKey partitionKey, IReadOnlyList<string> partitionKeyPath)
        {
            var staged = new List<Stream>();
            var batch = new Mock<TransactionalBatch>();
            batch.Setup(b => b.CreateItemStream(It.IsAny<Stream>(), It.IsAny<TransactionalBatchItemRequestOptions>()))
                 .Callback<Stream, TransactionalBatchItemRequestOptions>((stream, _) => staged.Add(stream))
                 .Returns(batch.Object);

            var handle = new CosmosAtomicWriteHandle(Mock.Of<Container>(), batch.Object, partitionKey, partitionKeyPath);
            var surface = new DocumentTierReliabilitySurface { CurrentHandle = handle };
            return (new CosmosBrokeredMessageOutbox(surface), surface, staged, batch);
        }

        private static JsonElement ReadStagedDocument(Stream stream)
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        [Fact]
        public async Task MustStageOutboxDocumentWithReservedDiscriminatorAndPendingStatus()
        {
            var (outbox, _, staged, _) = Harness(new PartitionKey("tenant-1"), Array.AsReadOnly(new[] { "/tenantId" }));

            await outbox.SendToOutbox(Message(), transactionContext: null);

            var document = ReadStagedDocument(staged.Should().ContainSingle().Subject);
            document.GetProperty("_chatterType").GetString().Should().Be("outbox");
            document.GetProperty("status").GetString().Should().Be("pending");
        }

        [Fact]
        public async Task MustEncodeIdAndStoreRawMessageIdVerbatim()
        {
            var (outbox, _, staged, _) = Harness(new PartitionKey("tenant-1"), Array.AsReadOnly(new[] { "/tenantId" }));
            var rawMessageId = "msg/with?reserved#chars";

            await outbox.SendToOutbox(Message(rawMessageId), transactionContext: null);

            var document = ReadStagedDocument(staged[0]);
            document.GetProperty("id").GetString().Should().Be(CosmosItemId.ForOutbox(rawMessageId));
            document.GetProperty("id").GetString().Should().StartWith("outbox:");
            // The encoded id segment must not carry Cosmos-forbidden characters lifted from the raw id.
            document.GetProperty("id").GetString().Should().NotContainAny("/", "\\", "?", "#");
            document.GetProperty("MessageId").GetString().Should().Be(rawMessageId);
        }

        [Fact]
        public async Task MustStampPartitionKeyValueAtContainerPathNotFixedField()
        {
            var (outbox, _, staged, _) = Harness(new PartitionKey("tenant-1"), Array.AsReadOnly(new[] { "/tenantId" }));

            await outbox.SendToOutbox(Message(), transactionContext: null);

            var document = ReadStagedDocument(staged[0]);
            document.GetProperty("tenantId").GetString().Should().Be("tenant-1");
            document.TryGetProperty("partitionKey", out _).Should().BeFalse("the value rides the container's actual PK path, not a fixed field");
        }

        [Fact]
        public async Task MustStampHierarchicalPartitionKeyValuesAtNestedPaths()
        {
            var partitionKey = new PartitionKeyBuilder().Add("acme").Add("us-east").Build();
            var (outbox, _, staged, _) = Harness(partitionKey, Array.AsReadOnly(new[] { "/tenant/id", "/region" }));

            await outbox.SendToOutbox(Message(), transactionContext: null);

            var document = ReadStagedDocument(staged[0]);
            document.GetProperty("tenant").GetProperty("id").GetString().Should().Be("acme");
            document.GetProperty("region").GetString().Should().Be("us-east");
        }

        [Fact]
        public async Task MustSerializeMessageBodyDestinationAndContentType()
        {
            var (outbox, _, staged, _) = Harness(new PartitionKey("tenant-1"), Array.AsReadOnly(new[] { "/tenantId" }));
            var message = Message();

            await outbox.SendToOutbox(message, transactionContext: null);

            var document = ReadStagedDocument(staged[0]);
            document.GetProperty("Destination").GetString().Should().Be("destination-queue");
            document.GetProperty("MessageBody").GetString().Should().Be(message.Stringify());
            document.GetProperty("MessageContentType").GetString().Should().Be(message.ContentType);
            // MessageContext is serialized with ChatterJson.Options to match the EF provider.
            var expectedContext = JsonSerializer.Serialize(message.MessageContext, ChatterJson.Options);
            document.GetProperty("MessageContext").GetString().Should().Be(expectedContext);
        }

        [Fact]
        public async Task MustCountStagedOperationWithoutExecuting()
        {
            var (outbox, surface, staged, batch) = Harness(new PartitionKey("tenant-1"), Array.AsReadOnly(new[] { "/tenantId" }));

            await outbox.SendToOutbox(Message(), transactionContext: null);

            surface.CurrentHandle.StagedOperationCount.Should().Be(1);
            staged.Should().ContainSingle();
            // The outbox contributes an op only; the behavior owns execute (framework-owns-batch-lifecycle).
            batch.Verify(b => b.ExecuteAsync(It.IsAny<System.Threading.CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MustFailWhenNoActiveHandleOnSurface()
        {
            IBrokeredMessageOutbox outbox = new CosmosBrokeredMessageOutbox(new DocumentTierReliabilitySurface());

            Func<Task> act = () => outbox.SendToOutbox(Message(), transactionContext: null);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustFailWhenActiveHandleDoesNotSupportReservedStaging()
        {
            // Wiring-error guard: the outbox authors a reserved outbox: id, so it needs the framework handle that
            // implements ICosmosReservedWriteHandle. A handle that satisfies ICosmosAtomicWriteHandle but NOT the
            // reserved facet is a wiring error and must fail loudly.
            var nonReservedHandle = new Mock<ICosmosAtomicWriteHandle>();
            nonReservedHandle.SetupGet(h => h.PartitionKey).Returns(new PartitionKey("tenant-1"));
            nonReservedHandle.SetupGet(h => h.PartitionKeyPath).Returns(Array.AsReadOnly(new[] { "/tenantId" }));
            var surface = new DocumentTierReliabilitySurface { CurrentHandle = nonReservedHandle.Object };
            IBrokeredMessageOutbox outbox = new CosmosBrokeredMessageOutbox(surface);

            Func<Task> act = () => outbox.SendToOutbox(Message(), transactionContext: null);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustPreserveNumericPartitionKeyValueKind()
        {
            // A numeric partition key opened on PartitionKey(42d) must be stamped as a JSON number, not the string "42",
            // so the document lands in the SAME logical partition the framework-owned batch was scoped to.
            var (outbox, _, staged, _) = Harness(new PartitionKey(42d), Array.AsReadOnly(new[] { "/tenantId" }));

            await outbox.SendToOutbox(Message(), transactionContext: null);

            var document = ReadStagedDocument(staged[0]);
            document.GetProperty("tenantId").ValueKind.Should().Be(JsonValueKind.Number);
            document.GetProperty("tenantId").GetDouble().Should().Be(42d);
        }

        [Fact]
        public async Task MustPreserveBooleanPartitionKeyValueKind()
        {
            var (outbox, _, staged, _) = Harness(new PartitionKey(true), Array.AsReadOnly(new[] { "/isActive" }));

            await outbox.SendToOutbox(Message(), transactionContext: null);

            var document = ReadStagedDocument(staged[0]);
            document.GetProperty("isActive").ValueKind.Should().Be(JsonValueKind.True);
            document.GetProperty("isActive").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task MustPreserveNullPartitionKeyValueKind()
        {
            var (outbox, _, staged, _) = Harness(PartitionKey.Null, Array.AsReadOnly(new[] { "/tenantId" }));

            await outbox.SendToOutbox(Message(), transactionContext: null);

            var document = ReadStagedDocument(staged[0]);
            document.GetProperty("tenantId").ValueKind.Should().Be(JsonValueKind.Null);
        }

        [Fact]
        public async Task MustPreserveMixedHierarchicalPartitionKeyValueKinds()
        {
            var partitionKey = new PartitionKeyBuilder().Add("acme").Add(7d).Build();
            var (outbox, _, staged, _) = Harness(partitionKey, Array.AsReadOnly(new[] { "/tenant/id", "/shard" }));

            await outbox.SendToOutbox(Message(), transactionContext: null);

            var document = ReadStagedDocument(staged[0]);
            document.GetProperty("tenant").GetProperty("id").GetString().Should().Be("acme");
            document.GetProperty("shard").ValueKind.Should().Be(JsonValueKind.Number);
            document.GetProperty("shard").GetDouble().Should().Be(7d);
        }

        [Theory]
        [InlineData("/id")]
        [InlineData("/_chatterType")]
        [InlineData("/status")]
        [InlineData("/MessageId")]
        [InlineData("/Destination")]
        [InlineData("/MessageBody")]
        [InlineData("/MessageContentType")]
        [InlineData("/MessageContext")]
        public async Task MustFailLoudlyWhenPartitionKeyPathCollidesWithReservedRootField(string reservedPath)
        {
            var (outbox, _, _, _) = Harness(new PartitionKey("tenant-1"), Array.AsReadOnly(new[] { reservedPath }));

            Func<Task> act = () => outbox.SendToOutbox(Message(), transactionContext: null);

            // A reserved-root collision must throw rather than silently overwrite a required Chatter field
            // (e.g. /id would replace the deterministic outbox id, colliding every doc in the partition).
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustFailLoudlyWhenNestedPartitionKeyPathRootCollidesWithReservedField()
        {
            // A nested path under a reserved root (e.g. /MessageContext/tenant) also overwrites a reserved scalar.
            var (outbox, _, _, _) = Harness(new PartitionKey("tenant-1"), Array.AsReadOnly(new[] { "/MessageContext/tenant" }));

            Func<Task> act = () => outbox.SendToOutbox(Message(), transactionContext: null);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustStageAllDocumentsWithoutReparentThrowForTwoMessagesAndSinglePath()
        {
            // Regression: shared JsonNode instances from the first doc were re-stamped into the second doc,
            // causing an already-parented-node exception. Each doc must receive its own fresh JsonNode.
            var (outbox, surface, staged, batch) = Harness(new PartitionKey("tenant-1"), Array.AsReadOnly(new[] { "/tenantId" }));

            await outbox.SendToOutbox(Messages(2), transactionContext: null);

            surface.CurrentHandle.StagedOperationCount.Should().Be(2);
            staged.Should().HaveCount(2);
            batch.Verify(b => b.ExecuteAsync(It.IsAny<System.Threading.CancellationToken>()), Times.Never);

            var doc0 = ReadStagedDocument(staged[0]);
            var doc1 = ReadStagedDocument(staged[1]);
            doc0.GetProperty("tenantId").GetString().Should().Be("tenant-1");
            doc1.GetProperty("tenantId").GetString().Should().Be("tenant-1");
            doc0.GetProperty("MessageId").GetString().Should().Be("msg-0");
            doc1.GetProperty("MessageId").GetString().Should().Be("msg-1");
        }

        [Fact]
        public async Task MustStageAllDocumentsWithFreshNodesForTwoMessagesAndHierarchicalPath()
        {
            // Regression: hierarchical paths stamp multiple segments; shared nodes across docs throw on reparent.
            var partitionKey = new PartitionKeyBuilder().Add("acme").Add("us-east").Build();
            var (outbox, surface, staged, batch) = Harness(partitionKey, Array.AsReadOnly(new[] { "/tenant/id", "/region" }));

            await outbox.SendToOutbox(Messages(2), transactionContext: null);

            surface.CurrentHandle.StagedOperationCount.Should().Be(2);
            staged.Should().HaveCount(2);
            batch.Verify(b => b.ExecuteAsync(It.IsAny<System.Threading.CancellationToken>()), Times.Never);

            foreach (var stream in staged)
            {
                var doc = ReadStagedDocument(stream);
                doc.GetProperty("tenant").GetProperty("id").GetString().Should().Be("acme");
                doc.GetProperty("region").GetString().Should().Be("us-east");
            }
        }

        [Fact]
        public async Task MustPreserveNumericPartitionKeyKindOnEveryDocumentInMultiMessageDrain()
        {
            // Regression lock: value-kind must survive on doc #2 and beyond, not only on doc #1.
            var (outbox, _, staged, _) = Harness(new PartitionKey(42d), Array.AsReadOnly(new[] { "/tenantId" }));

            await outbox.SendToOutbox(Messages(2), transactionContext: null);

            staged.Should().HaveCount(2);
            foreach (var stream in staged)
            {
                var doc = ReadStagedDocument(stream);
                doc.GetProperty("tenantId").ValueKind.Should().Be(JsonValueKind.Number);
                doc.GetProperty("tenantId").GetDouble().Should().Be(42d);
            }
        }

        [Fact]
        public async Task MustPreserveMixedKindPartitionKeyOnEveryDocumentInMultiMessageDrain()
        {
            // Regression lock: mixed string+number hierarchical PK must survive on every doc, not only doc #1.
            var partitionKey = new PartitionKeyBuilder().Add("acme").Add(7d).Build();
            var (outbox, _, staged, _) = Harness(partitionKey, Array.AsReadOnly(new[] { "/tenant/id", "/shard" }));

            await outbox.SendToOutbox(Messages(3), transactionContext: null);

            staged.Should().HaveCount(3);
            foreach (var stream in staged)
            {
                var doc = ReadStagedDocument(stream);
                doc.GetProperty("tenant").GetProperty("id").GetString().Should().Be("acme");
                doc.GetProperty("shard").ValueKind.Should().Be(JsonValueKind.Number);
                doc.GetProperty("shard").GetDouble().Should().Be(7d);
            }
        }
    }
}
