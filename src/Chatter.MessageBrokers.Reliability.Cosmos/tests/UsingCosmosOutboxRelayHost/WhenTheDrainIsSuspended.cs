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
        // A Lease Token is a partition-key-range id of the monitored container it came from, so two distinct sources
        // on one host routinely report the SAME token.
        private const string SharedLeaseToken = "0";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        [Fact]
        public async Task MustRefuseToPublishOnceConfirmationsKeepFailing()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published, OutboxDrainGate drainGate) = HostRecordingPublishes();
            Container container = ThrowingContainer(stampFailure);

            await FailConfirmationsAsync(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);

            Func<Task> act = () => DrainAsync(host, container, drainGate, LeaseToken);

            await act.Should().ThrowAsync<InvalidOperationException>(
                "a suspended lease takes the same fail-closed exit a publish failure takes, so the SDK does not checkpoint the batch");
            published.Should().HaveCount(OutboxDrainGate.Threshold,
                "the suspended batch is refused ABOVE the document loop, so it republishes nothing");
        }

        [Fact]
        public async Task MustRethrowTheOriginalConfirmationFaultRatherThanTheCarrier()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published, OutboxDrainGate drainGate) = HostRecordingPublishes();

            Func<Task> act = () => DrainAsync(host, ThrowingContainer(stampFailure), drainGate, LeaseToken);

            Exception thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
            thrown.Should().BeSameAs(stampFailure,
                "the internal Confirmation Failure carrier never escapes the module, so error.type on the drain-failure count is unchanged");
            published.Should().ContainSingle("the document published before its confirmation failed");
        }

        [Fact]
        public async Task MustKeepDrainingEveryOtherLease()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published, OutboxDrainGate drainGate) = HostRecordingPublishes();
            Container container = ThrowingContainer(stampFailure);

            await FailConfirmationsAsync(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);
            published.Clear();

            Func<Task> act = () => DrainAsync(host, container, drainGate, "lease-1");

            await act.Should().ThrowAsync<InvalidOperationException>("this lease's own confirmation failed too");
            published.Should().ContainSingle("a suspension is raised against ONE lease and never against the host");
        }

        [Fact]
        public async Task MustKeepDrainingOnceAConfirmationSucceeds()
        {
            var stampFailure = new InvalidOperationException("the container is not accepting the delivered stamp");
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published, OutboxDrainGate drainGate) = HostRecordingPublishes();

            await FailConfirmationsAsync(host, ThrowingContainer(stampFailure), drainGate, LeaseToken, OutboxDrainGate.Threshold - 1);
            await DrainAsync(host, StampingContainer(), drainGate, LeaseToken);
            await FailConfirmationsAsync(host, ThrowingContainer(stampFailure), drainGate, LeaseToken, OutboxDrainGate.Threshold - 1);
            published.Clear();

            Func<Task> act = () => DrainAsync(host, ThrowingContainer(stampFailure), drainGate, LeaseToken);

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
            (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> _, OutboxDrainGate drainGate) = HostRecordingPublishes();
            Container container = ThrowingContainer(stampFailure);

            await FailConfirmationsAsync(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                Func<Task> act = () => DrainAsync(host, container, drainGate, LeaseToken);
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
            OutboxDrainGate drainGate = ProcessorGate();

            await FailConfirmationsAsync(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);
            Func<Task> act = () => DrainAsync(host, container, drainGate, LeaseToken);
            await act.Should().ThrowAsync<InvalidOperationException>();

            processor.Verify(p => p.StopAsync(), Times.Never,
                "a Drain Suspension declines to publish a lease; it stops no processor and no hosted service");
            host.TrackedProcessors.Should().ContainSingle("the host still owns every processor it started");
        }

        /// <summary>
        /// TWO distinct Change-Feed Source Identities on ONE host — the multi-container shape ADR-0008 supports — both
        /// draining a Lease Token named "0", because a Lease Token is a partition-key-range id of its OWN monitored
        /// container and every container has a range "0". The healthy source's confirmations must not touch the
        /// failing source's consecutive count: if they did, the failing source would never reach the threshold and its
        /// documents would republish forever, which is the defect the suspension exists to close.
        /// </summary>
        [Fact]
        public async Task MustNotLetOneSourcesConfirmationSuccessResetAnothersFailureCount()
        {
            var stampFailure = new TimeoutException("the container is not accepting the delivered stamp");
            (IReadOnlyList<Container.ChangeFeedStreamHandler> handlers, List<OutboundBrokeredMessage> published) =
                await StartTwoSourceHostAsync(stampFailure);

            foreach (int _ in Enumerable.Range(0, OutboxDrainGate.Threshold))
            {
                Func<Task> failingSource = () => DrainThroughHandlerAsync(handlers[0], SharedLeaseToken);
                await failingSource.Should().ThrowAsync<TimeoutException>();
                await DrainThroughHandlerAsync(handlers[1], SharedLeaseToken);
            }

            published.Clear();
            Func<Task> act = () => DrainThroughHandlerAsync(handlers[0], SharedLeaseToken);

            await act.Should().ThrowAsync<InvalidOperationException>(
                "the healthy source drains its OWN lease, so its confirmations cannot evict the failing source's count");
            published.Should().BeEmpty("a suspended source republishes nothing");
        }

        /// <summary>
        /// A Drain Suspension raised by one Change-Feed Source Identity must not refuse another source's batch that
        /// happens to arrive on an identically-named Lease Token. The healthy source is a different monitored
        /// container whose confirmation path is up; refusing it would stop delivery that was about to complete.
        /// </summary>
        [Fact]
        public async Task MustKeepDrainingASecondSourceOnAnIdenticallyNamedLeaseToken()
        {
            var stampFailure = new TimeoutException("the container is not accepting the delivered stamp");
            (IReadOnlyList<Container.ChangeFeedStreamHandler> handlers, List<OutboundBrokeredMessage> published) =
                await StartTwoSourceHostAsync(stampFailure);

            await FailConfirmationsThroughHandlerAsync(handlers[0], SharedLeaseToken, OutboxDrainGate.Threshold);
            published.Clear();

            await DrainThroughHandlerAsync(handlers[1], SharedLeaseToken);

            published.Should().ContainSingle(
                "a suspension is scoped to the processor that raised it, so another source's lease keeps draining");
        }

        /// <summary>
        /// The dashboard question the Lease Token alone cannot answer. TWO Change-Feed Source Identities on ONE host
        /// both drain a lease named "0", so every lease-tagged measurement they emit lands on the same series unless
        /// it also carries the source. An operator reading a suspended or stalled "0" could not tell WHICH source
        /// stopped — which is the same ambiguity the per-processor gate closes in the relay's control flow, closed
        /// here on the telemetry.
        /// </summary>
        [Fact]
        public async Task MustReportEachSourcesBatchesUnderItsOwnSourceIdentity()
        {
            var stampFailure = new TimeoutException("the container is not accepting the delivered stamp");
            (IReadOnlyList<Container.ChangeFeedStreamHandler> handlers, List<OutboundBrokeredMessage> _) =
                await StartTwoSourceHostAsync(stampFailure);

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                Func<Task> failingSource = () => DrainThroughHandlerAsync(handlers[0], SharedLeaseToken);
                await failingSource.Should().ThrowAsync<TimeoutException>();
                await DrainThroughHandlerAsync(handlers[1], SharedLeaseToken);

                var batches = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedBatchesInstrumentName).Should().HaveCount(2).And.Subject;

                batches.Should().OnlyContain(measurement => TagOf(measurement, CosmosReliabilityDiagnostics.LeaseToken) == SharedLeaseToken,
                    "both sources drained a partition-key range named \"0\", which is why the lease token alone cannot separate them");
                batches.Select(measurement => TagOf(measurement, CosmosReliabilityDiagnostics.SourceIdentity))
                       .Should().OnlyHaveUniqueItems("each processor reports under the Change-Feed Source Identity it drains")
                       .And.NotContainNulls();
            }
        }

        private static string TagOf(RecordedMeasurement measurement, string tagName)
            => measurement.TryGetTag(tagName, out var value) ? value?.ToString() : null;

        // Starts a host owning TWO processors over two distinct declared Change-Feed Source Identities and returns each
        // processor's OWN change-feed handler, captured through the factory seam. The first source's monitored
        // container refuses the delivered stamp; the second's accepts it.
        private static async Task<(IReadOnlyList<Container.ChangeFeedStreamHandler> handlers, List<OutboundBrokeredMessage> published)> StartTwoSourceHostAsync(Exception stampFailure)
        {
            var registry = new DocumentReliabilityRegistry();
            registry.Add(RegistrationFor(typeof(DrainedCommand), "source-a", VerifiablyConfiguredContainer(ThrowingContainerMock(stampFailure))));
            registry.Add(RegistrationFor(typeof(SecondDrainedCommand), "source-b", VerifiablyConfiguredContainer(StampingContainerMock())));

            (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> published) = RecordingProvider();
            var host = new CosmosOutboxRelayHostedService(
                registry,
                new CosmosContainerFactory(Mock.Of<IServiceProvider>()),
                provider,
                BodyConverterFactory());

            var handlers = new List<Container.ChangeFeedStreamHandler>();
            host.ProcessorFactory = (_, __, onChanges) => CaptureHandler(handlers, onChanges);

            await host.StartAsync(CancellationToken.None);
            return (handlers, published);
        }

        private static ChangeFeedProcessor CaptureHandler(List<Container.ChangeFeedStreamHandler> handlers, Container.ChangeFeedStreamHandler onChanges)
        {
            handlers.Add(onChanges);
            return StartableProcessor().Object;
        }

        // A registration on the ADVANCED (declared source identity) path, so the two registrations are two distinct
        // Change-Feed Source Identities and the host builds one processor for each.
        private static DocumentReliabilityRegistration RegistrationFor(Type commandType, string sourceIdentity, Container monitoredContainer)
            => new DocumentReliabilityRegistration(
                commandType,
                "shop",
                sourceIdentity + ":documents",
                sourceIdentity + ":leases",
                _ => new PartitionKey("tenant-1"),
                PartitionKeyPath,
                documentContainerFactory: _ => monitoredContainer,
                leaseContainerFactory: _ => Mock.Of<Container>(),
                declaredSourceIdentity: new CosmosSourceIdentity(sourceIdentity, sourceIdentity + "-lease"));

        // Drives ONE change-feed batch through ONE processor's OWN handler, which is the only way production reaches a
        // gate: the SDK hands the handler the Lease Token the batch arrived on.
        private static async Task DrainThroughHandlerAsync(Container.ChangeFeedStreamHandler handler, string leaseToken)
        {
            using Stream batch = BatchOf(OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1"));
            await handler(LeaseContext(leaseToken), batch, CancellationToken.None);
        }

        private static async Task FailConfirmationsThroughHandlerAsync(Container.ChangeFeedStreamHandler handler, string leaseToken, int count)
        {
            foreach (int _ in Enumerable.Range(0, count))
            {
                Func<Task> act = () => DrainThroughHandlerAsync(handler, leaseToken);
                await act.Should().ThrowAsync<TimeoutException>();
            }
        }

        private static ChangeFeedProcessorContext LeaseContext(string leaseToken)
        {
            var context = new Mock<ChangeFeedProcessorContext>();
            context.SetupGet(c => c.LeaseToken).Returns(leaseToken);
            return context.Object;
        }

        // Drives ONE change-feed batch carrying ONE pending Outbox Document through the host's stream handler, on the
        // gate that ONE processor drains through — which is what BuildChangeFeedHandler hands the handler in production.
        private static async Task DrainAsync(CosmosOutboxRelayHostedService host, Container container, OutboxDrainGate drainGate, string leaseToken)
        {
            using Stream batch = BatchOf(OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1"));
            await host.HandleChangesAsync(batch, container, PartitionKeyPath, leaseToken, drainGate, CancellationToken.None);
        }

        // Drains <paramref name="count"/> batches whose documents publish and whose confirmations all fail, which is
        // the wedged lease the suspension exists for: the batch is never checkpointed and re-surfaces unchanged.
        private static async Task FailConfirmationsAsync(CosmosOutboxRelayHostedService host, Container container, OutboxDrainGate drainGate, string leaseToken, int count)
        {
            foreach (int _ in Enumerable.Range(0, count))
            {
                Func<Task> act = () => DrainAsync(host, container, drainGate, leaseToken);
                await act.Should().ThrowAsync<InvalidOperationException>();
            }
        }

        // The gate ONE processor drains through, standing in for the one BuildChangeFeedHandler constructs per
        // descriptor. It is deliberately NOT reachable from the host: no host owns a gate any more. The gate is also
        // what carries the Change-Feed Source Identity its suspensions and batches are reported under.
        private static OutboxDrainGate ProcessorGate()
            => new OutboxDrainGate("chatter-cosmos-outbox-relay:source-under-test", new GuardedRelayLog(logger: null));

        private static (CosmosOutboxRelayHostedService host, List<OutboundBrokeredMessage> published, OutboxDrainGate drainGate) HostRecordingPublishes()
        {
            (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> published) = RecordingProvider();
            var host = new CosmosOutboxRelayHostedService(
                new DocumentReliabilityRegistry(),
                new CosmosContainerFactory(Mock.Of<IServiceProvider>()),
                provider,
                BodyConverterFactory());
            return (host, published, ProcessorGate());
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
                documentContainerFactory: _ => VerifiablyConfiguredContainer(new Mock<Container>()),
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

        private sealed class SecondDrainedCommand : CQRS.Commands.ICommand { }

        private static Mock<ChangeFeedProcessor> StartableProcessor()
        {
            var processor = new Mock<ChangeFeedProcessor>();
            processor.Setup(p => p.StartAsync()).Returns(Task.CompletedTask);
            processor.Setup(p => p.StopAsync()).Returns(Task.CompletedTask);
            return processor;
        }

        // Gives a monitored container ground truth that MATCHES its declared configuration, so the host's start-time
        // verification pass passes and the processors are built.
        private static Container VerifiablyConfiguredContainer(Mock<Container> container)
        {
            var properties = new ContainerProperties("documents", PartitionKeyPath)
            {
                DefaultTimeToLive = -1,
            };

            var response = new Mock<ContainerResponse>();
            response.SetupGet(r => r.Resource).Returns(properties);

            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns("shop");

            container.SetupGet(c => c.Id).Returns("documents");
            container.SetupGet(c => c.Database).Returns(database.Object);
            container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Returns(() => Task.FromResult(response.Object));
            return container.Object;
        }

        // A container whose every patch fails, standing in for one that is not accepting the delivered stamp.
        private static Container ThrowingContainer(Exception toThrow) => ThrowingContainerMock(toThrow).Object;

        private static Mock<Container> ThrowingContainerMock(Exception toThrow)
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(toThrow);
            return container;
        }

        // A container that accepts the delivered stamp, which is the confirmation path having come back.
        private static Container StampingContainer() => StampingContainerMock().Object;

        private static Mock<Container> StampingContainerMock()
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Mock.Of<ItemResponse<JsonElement>>());
            return container;
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
