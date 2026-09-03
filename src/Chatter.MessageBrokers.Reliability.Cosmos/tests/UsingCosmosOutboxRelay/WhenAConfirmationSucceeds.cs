using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelay
{
    /// <summary>
    /// The Confirmation Receipt the relay mints when its status write LANDS. A host lifts a Drain Suspension only on
    /// this evidence, so what the relay does and does NOT mint is what makes "a batch that confirmed nothing resumed
    /// draining" unrepresentable.
    /// </summary>
    /// <remarks>
    /// The receipt is minted at the ONE write both stamps share, so evidence follows the write rather than following
    /// which branch the drain happened to take.
    /// </remarks>
    public class WhenAConfirmationSucceeds : Testing.Core.Context
    {
        private const string InfrastructureType = "test-infra";
        private const string MessageId = "msg-1";
        private const string TenantId = "tenant-1";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        /// <summary>
        /// The delivered stamp landing is the confirmation the suspension measures: the document published and the
        /// monitored container accepted the write that marks it delivered.
        /// </summary>
        [Fact]
        public async Task MustMintAReceiptWhenTheDeliveredStampLands()
        {
            var relay = new CosmosOutboxRelay(RecordingProvider(), BodyConverterFactory());

            ConfirmationReceipt receipt = await relay.ProcessChangeAsync(PendingOutboxDocument(), StampingContainer(), PartitionKeyPath);

            receipt.IsPresent.Should().BeTrue("the response the delivered stamp returned is the evidence the write landed");
        }

        /// <summary>
        /// The give-up stamp counts as evidence too. It is the SAME single write, at the same status path, under the
        /// same recovered partition key: one that lands proves the write path whose failure opens a suspension is up.
        /// </summary>
        [Fact]
        public async Task MustMintAReceiptWhenTheUndeliverableStampLands()
        {
            var relay = new CosmosOutboxRelay(RecordingProvider(), BodyConverterFactory());

            // No destination: OutboundBrokeredMessage's constructor rejects one, so the contract stamps this document
            // undeliverable instead of publishing it.
            ConfirmationReceipt receipt = await relay.ProcessChangeAsync(PendingOutboxDocument(destination: null), StampingContainer(), PartitionKeyPath);

            receipt.IsPresent.Should().BeTrue("the give-up stamp is the same status write a delivered stamp performs");
        }

        /// <summary>
        /// A document the relay did not admit performed NO status write, so it is evidence of nothing. The monitored
        /// container is CO-RESIDENT by design, so this is the ORDINARY document on the feed — which is exactly why a
        /// drain loop reaching its end cannot stand in for a confirmation.
        /// </summary>
        [Fact]
        public async Task MustMintNoReceiptForADocumentItDidNotAdmit()
        {
            var relay = new CosmosOutboxRelay(RecordingProvider(), BodyConverterFactory());

            ConfirmationReceipt receipt = await relay.ProcessChangeAsync(DomainDocument(), StampingContainer(), PartitionKeyPath);

            receipt.IsPresent.Should().BeFalse("nothing was written, so there is nothing to confirm");
        }

        /// <summary>
        /// A status write that THREW mints nothing at all: the call does not return, so no receipt can reach a gate.
        /// The drop path is used here because it propagates the stamp fault untouched — no publish happened, so there
        /// is no Confirmation Failure carrier to unwrap.
        /// </summary>
        [Fact]
        public async Task MustMintNoReceiptWhenTheStampThrows()
        {
            var stampFailure = new TimeoutException("the container is not accepting the status write");
            var relay = new CosmosOutboxRelay(RecordingProvider(), BodyConverterFactory());

            Func<Task<ConfirmationReceipt>> act = () => relay.ProcessChangeAsync(
                PendingOutboxDocument(), ThrowingContainer(stampFailure), PartitionKeyPath, ResolverReturning(toReturn: null));

            (await act.Should().ThrowAsync<TimeoutException>(
                "a write that did not land returns no response, so it mints no evidence")).Which.Should().BeSameAs(stampFailure);
        }

        // The exact outbox wire document the Document-Tier Batch-Lifecycle Behavior writes. A null destination OMITS
        // that property, which is the contract violation that routes the document to the give-up stamp.
        private static JsonElement PendingOutboxDocument(string destination = "orders")
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = CosmosItemId.ForOutbox(MessageId),
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.MessageIdField] = MessageId,
                [CosmosOutboxDocument.MessageBodyField] = "{}",
                [CosmosOutboxDocument.MessageContentTypeField] = "application/json",
                [CosmosOutboxDocument.MessageContextField] = new JsonObject { [MessageContext.InfrastructureType] = InfrastructureType }.ToJsonString(),
                ["tenantId"] = TenantId,
            };

            if (destination is not null)
            {
                node[CosmosOutboxDocument.DestinationField] = destination;
            }

            return Parse(node.ToJsonString());
        }

        // A co-resident domain write: no Chatter discriminator at all, so the relay never admits it.
        private static JsonElement DomainDocument()
            => Parse(new JsonObject
            {
                ["id"] = "order-1",
                ["tenantId"] = TenantId,
            }.ToJsonString());

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static IBodyConverterFactory BodyConverterFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        private static IMessagingInfrastructureProvider RecordingProvider()
        {
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .Returns(Task.CompletedTask);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return provider.Object;
        }

        // A container whose status write LANDS, returning the response the receipt is minted from.
        private static Container StampingContainer()
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Mock.Of<ItemResponse<JsonElement>>());
            return container.Object;
        }

        // A container that is not accepting the status write.
        private static Container ThrowingContainer(Exception toThrow)
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(toThrow);
            return container.Object;
        }

        // A resolver that returns the supplied message, which may be null — the drop-and-acknowledge path.
        private static IOutboxBodyResolver ResolverReturning(OutboundBrokeredMessage toReturn)
        {
            var resolver = new Mock<IOutboxBodyResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<OutboxDrainContext>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(toReturn);
            return resolver.Object;
        }
    }
}
