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
    /// The #416 Confirmation Failure carrier: the message PUBLISHED and the delivered stamp did not land, so the batch
    /// is not checkpointed and the same document publishes again on every pass. The relay raises the carrier at that
    /// one site so the fault survives the unwind to the host, which is the only component that knows the Lease Token it
    /// happened under.
    /// </summary>
    /// <remarks>
    /// THIS IS NOT EXCEPTION-TYPE SNIFFING. The phase is derived from the relay's OWN control flow — whether the
    /// dispatch returned — never from what the stamp threw, which is why the no-publish paths below raise nothing.
    /// </remarks>
    public class WhenAConfirmationFails : Testing.Core.Context
    {
        private const string InfrastructureType = "test-infra";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        // The exact outbox wire document the Document-Tier Batch-Lifecycle Behavior writes, as a JsonElement the relay
        // reads (parity with WhenDrainingOutbox's builder).
        private static JsonElement OutboxDocument(string messageId, string destination, object body, string tenantId)
        {
            var converter = new JsonBodyConverter();
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InfrastructureType,
            };
            var outbound = new OutboundBrokeredMessage(messageId, body, messageContext, destination, converter);
            CosmosOutboxDocument document = CosmosOutboxDocument.From(outbound);

            var partitionKeyValues = new List<JsonElement> { JsonValue(tenantId) };
            JsonObject rendered = document.ToJsonObject(PartitionKeyPath, partitionKeyValues);
            return Parse(rendered.ToJsonString());
        }

        private static JsonElement JsonValue(string raw) => Parse(JsonSerializer.Serialize(raw));

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

        // A provider whose dispatcher records every dispatched message; the recorded list is the publish ledger.
        private static (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> published) RecordingProvider()
        {
            var published = new List<OutboundBrokeredMessage>();
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => published.Add(m))
                      .Returns(Task.CompletedTask);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return (provider.Object, published);
        }

        // A provider whose dispatcher throws on publish — the publish-failure path.
        private static IMessagingInfrastructureProvider ThrowingProvider(Exception toThrow)
        {
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .ThrowsAsync(toThrow);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return provider.Object;
        }

        // A container whose every patch fails, standing in for one that is not accepting the delivered stamp.
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

        [Fact]
        public async Task MustRaiseTheCarrierWrappingTheStampFaultWhenTheDocumentPublished()
        {
            var (provider, published) = RecordingProvider();
            var stampFailure = new InvalidOperationException("the container is unavailable");
            Container container = ThrowingContainer(stampFailure);
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            Func<Task> act = () => relay.ProcessChangeAsync(document, container, PartitionKeyPath);

            OutboxConfirmationFailedException carrier =
                (await act.Should().ThrowAsync<OutboxConfirmationFailedException>(
                    "the message published and could not be marked delivered, so every redrain republishes it")).Which;
            carrier.InnerException.Should().BeSameAs(stampFailure, "the host unwraps the carrier and rethrows the ORIGINAL fault");
            published.Should().ContainSingle("the carrier is reachable only AFTER a successful publish");
        }

        [Fact]
        public async Task MustNotRaiseTheCarrierWhenTheResolverPublishedNothing()
        {
            // A null resolution is an intentional drop: nothing published, so a failed stamp amplifies nothing. It is
            // an ordinary drain failure, not a Confirmation Failure.
            var (provider, published) = RecordingProvider();
            var stampFailure = new InvalidOperationException("the container is unavailable");
            Container container = ThrowingContainer(stampFailure);
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), OutboxDeliverySettings.Legacy);

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            Func<Task> act = () => relay.ProcessChangeAsync(document, container, PartitionKeyPath, ResolverReturning(toReturn: null));

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(stampFailure);
            published.Should().BeEmpty("no publish means no amplification to report");
        }

        [Fact]
        public async Task MustNotRaiseTheCarrierWhenThePublishItselfFailed()
        {
            var publishFailure = new InvalidOperationException("broker unavailable");
            IMessagingInfrastructureProvider provider = ThrowingProvider(publishFailure);
            Container container = ThrowingContainer(new InvalidOperationException("the container is unavailable"));
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            Func<Task> act = () => relay.ProcessChangeAsync(document, container, PartitionKeyPath);

            (await act.Should().ThrowAsync<InvalidOperationException>(
                "a publish that never landed is not a confirmation failure")).Which.Should().BeSameAs(publishFailure);
        }
    }
}
