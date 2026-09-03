using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelayHost
{
    /// <summary>
    /// The #416 Drain Suspension as the registry-driven relay host applies it: the confirmation path is down, so every
    /// pass publishes the same Outbox Document again at real broker, receiver and request-unit cost. The host is the
    /// only component that knows the Lease Token a Confirmation Failure happened under, so the host is what consults
    /// the gate — once per batch, before any document publishes.
    /// </summary>
    /// <remarks>
    /// A refused batch takes the module's EXISTING fail-closed exit: throw, so the SDK does not checkpoint and the
    /// batch re-surfaces. Nothing is halted — no Change-Feed Processor and no hosted service is stopped.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenTheDrainIsSuspended : Testing.Core.Context
    {
        private const string InfrastructureType = "test-infra";
        private const string LeaseToken = "lease-0";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        [Fact]
        public async Task MustRefuseToPublishOnceConfirmationsKeepFailing()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published) = HostRecordingPublishes();
            Container container = ThrowingContainer(stampFailure);

            await FailConfirmationsAsync(host, container, LeaseToken, OutboxDrainGate.Threshold);

            Func<Task> act = () => DrainAsync(host, container, LeaseToken);

            await act.Should().ThrowAsync<InvalidOperationException>(
                "a suspended lease takes the same fail-closed exit a publish failure takes, so the SDK does not checkpoint the batch");
            published.Should().HaveCount(OutboxDrainGate.Threshold,
                "the suspended batch is refused ABOVE the document loop, so it republishes nothing");
        }

        [Fact]
        public async Task MustRethrowTheOriginalConfirmationFaultRatherThanTheCarrier()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published) = HostRecordingPublishes();

            Func<Task> act = () => DrainAsync(host, ThrowingContainer(stampFailure), LeaseToken);

            Exception thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
            thrown.Should().BeSameAs(stampFailure,
                "the internal Confirmation Failure carrier never escapes the module, so error.type on the drain-failure count is unchanged");
            published.Should().ContainSingle("the document published before its confirmation failed");
        }

        [Fact]
        public async Task MustKeepDrainingEveryOtherLease()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published) = HostRecordingPublishes();
            Container container = ThrowingContainer(stampFailure);

            await FailConfirmationsAsync(host, container, LeaseToken, OutboxDrainGate.Threshold);
            published.Clear();

            Func<Task> act = () => DrainAsync(host, container, "lease-1");

            await act.Should().ThrowAsync<InvalidOperationException>("this lease's own confirmation failed too");
            published.Should().ContainSingle("a suspension is raised against ONE lease and never against the host");
        }

        [Fact]
        public async Task MustKeepDrainingOnceAConfirmationSucceeds()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published) = HostRecordingPublishes();

            await FailConfirmationsAsync(host, ThrowingContainer(stampFailure), LeaseToken, OutboxDrainGate.Threshold - 1);
            await DrainAsync(host, StampingContainer(), LeaseToken);
            await FailConfirmationsAsync(host, ThrowingContainer(stampFailure), LeaseToken, OutboxDrainGate.Threshold - 1);
            published.Clear();

            Func<Task> act = () => DrainAsync(host, ThrowingContainer(stampFailure), LeaseToken);

            await act.Should().ThrowAsync<InvalidOperationException>();
            published.Should().ContainSingle(
                "a successful confirmation clears the lease outright, so the consecutive count starts over rather than accumulating across a healthy stretch");
        }

        /// <summary>
        /// A suspended batch is still SIZED and COUNTED against its lease. The batch measurements are what keep a
        /// stalled lease distinguishable from an idle one, so the suspension is consulted BELOW them.
        /// </summary>
        [Fact]
        public async Task MustStillCountASuspendedBatchAgainstItsLease()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> _) = HostRecordingPublishes();
            Container container = ThrowingContainer(stampFailure);

            await FailConfirmationsAsync(host, container, LeaseToken, OutboxDrainGate.Threshold);

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                Func<Task> act = () => DrainAsync(host, container, LeaseToken);
                await act.Should().ThrowAsync<InvalidOperationException>();

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedBatchesInstrumentName).Should().ContainSingle(
                    "a stalled lease that stopped reporting batches would look exactly like an idle one");
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainBatchSizeInstrumentName).Should().ContainSingle();
            }
        }

        /// <summary>
        /// The suspension HALTS NOTHING. The relay declines to publish one lease; every Change-Feed Processor the host
        /// owns keeps running, because the co-resident pipeline host must be untouched by a suspension.
        /// </summary>
        [Fact]
        public async Task MustStopNoChangeFeedProcessor()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            Mock<ChangeFeedProcessor> processor = StartableProcessor();
            CosmosOutboxRelayHostedService host = HostWithProcessor(processor);
            await host.StartAsync(CancellationToken.None);
            Container container = ThrowingContainer(stampFailure);

            await FailConfirmationsAsync(host, container, LeaseToken, OutboxDrainGate.Threshold);
            Func<Task> act = () => DrainAsync(host, container, LeaseToken);
            await act.Should().ThrowAsync<InvalidOperationException>();

            processor.Verify(p => p.StopAsync(), Times.Never,
                "a Drain Suspension declines to publish a lease; it stops no processor and no hosted service");
            host.TrackedProcessors.Should().ContainSingle("the host still owns every processor it started");
        }

        // Drives ONE change-feed batch carrying ONE pending Outbox Document through the host's stream handler.
        private static async Task DrainAsync(CosmosOutboxRelayHostedService host, Container container, string leaseToken)
        {
            using Stream batch = BatchOf(OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1"));
            await host.HandleChangesAsync(batch, container, PartitionKeyPath, leaseToken, CancellationToken.None);
        }

        // Drains <paramref name="count"/> batches whose documents publish and whose confirmations all fail, which is
        // the wedged lease the suspension exists for: the batch is never checkpointed and re-surfaces unchanged.
        private static async Task FailConfirmationsAsync(CosmosOutboxRelayHostedService host, Container container, string leaseToken, int count)
        {
            foreach (int _ in Enumerable.Range(0, count))
            {
                Func<Task> act = () => DrainAsync(host, container, leaseToken);
                await act.Should().ThrowAsync<InvalidOperationException>();
            }
        }

        private static (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published) HostRecordingPublishes()
        {
            (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> published) = RecordingProvider();
            var host = new CosmosOutboxRelayHostedService(
                new DocumentReliabilityRegistry(),
                new CosmosContainerFactory(Mock.Of<IServiceProvider>()),
                provider,
                BodyConverterFactory());
            return (host, published);
        }

        // A host owning exactly one started Change-Feed Processor, so a suspension's effect on the processors the host
        // holds is observable. Mirrors WhenStartingProcessorsFails: the SDK's builder is sealed, so the host's factory
        // seam hands out the mockable processor.
        private static CosmosOutboxRelayHostedService HostWithProcessor(Mock<ChangeFeedProcessor> processor)
        {
            var registry = new DocumentReliabilityRegistry();
            registry.Add(new DocumentReliabilityRegistration(
                typeof(DrainedCommand),
                "shop",
                "documents",
                "leases",
                _ => new PartitionKey("tenant-1"),
                PartitionKeyPath,
                documentContainerFactory: _ => VerifiablyConfiguredContainer(),
                leaseContainerFactory: _ => Mock.Of<Container>(),
                declaredSourceIdentity: new CosmosSourceIdentity("documents", "leases")));

            (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> _) = RecordingProvider();
            var host = new CosmosOutboxRelayHostedService(
                registry,
                new CosmosContainerFactory(Mock.Of<IServiceProvider>()),
                provider,
                BodyConverterFactory());
            host.ProcessorFactory = (_, __, ___) => processor.Object;
            return host;
        }

        private sealed class DrainedCommand : CQRS.Commands.ICommand { }

        private static Mock<ChangeFeedProcessor> StartableProcessor()
        {
            var processor = new Mock<ChangeFeedProcessor>();
            processor.Setup(p => p.StartAsync()).Returns(Task.CompletedTask);
            processor.Setup(p => p.StopAsync()).Returns(Task.CompletedTask);
            return processor;
        }

        // A monitored container whose ground truth MATCHES its declared configuration, so the host's start-time
        // verification pass passes and the processors are built.
        private static Container VerifiablyConfiguredContainer()
        {
            var properties = new ContainerProperties("documents", PartitionKeyPath)
            {
                DefaultTimeToLive = -1,
            };

            var response = new Mock<ContainerResponse>();
            response.SetupGet(r => r.Resource).Returns(properties);

            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns("shop");

            var container = new Mock<Container>();
            container.SetupGet(c => c.Id).Returns("documents");
            container.SetupGet(c => c.Database).Returns(database.Object);
            container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Returns(() => Task.FromResult(response.Object));
            return container.Object;
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

        // A container that accepts the delivered stamp, which is the confirmation path having come back.
        private static Container StampingContainer()
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Mock.Of<ItemResponse<JsonElement>>());
            return container.Object;
        }

        private static (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> published) RecordingProvider()
        {
            var published = new List<OutboundBrokeredMessage>();
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .Callback<OutboundBrokeredMessage, TransactionContext>((message, _) => published.Add(message))
                      .Returns(Task.CompletedTask);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return (provider.Object, published);
        }

        private static IBodyConverterFactory BodyConverterFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        // The change-feed batch wire shape the SDK hands the host's stream handler.
        private static Stream BatchOf(params JsonElement[] documents)
        {
            var payload = new JsonObject
            {
                ["Documents"] = new JsonArray(Array.ConvertAll(documents, document => JsonNode.Parse(document.GetRawText()))),
            };
            return new MemoryStream(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        }

        // The exact outbox wire document the Document-Tier Batch-Lifecycle Behavior writes.
        private static JsonElement OutboxDocument(string messageId, string destination, object body, string tenantId)
        {
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InfrastructureType,
            };
            var outbound = new OutboundBrokeredMessage(messageId, body, messageContext, destination, new JsonBodyConverter());
            CosmosOutboxDocument document = CosmosOutboxDocument.From(outbound);

            var partitionKeyValues = new List<JsonElement> { Parse(JsonSerializer.Serialize(tenantId)) };
            JsonObject rendered = document.ToJsonObject(PartitionKeyPath, partitionKeyValues);
            return Parse(rendered.ToJsonString());
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
    }
}
