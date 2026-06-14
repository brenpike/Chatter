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
    }
}
