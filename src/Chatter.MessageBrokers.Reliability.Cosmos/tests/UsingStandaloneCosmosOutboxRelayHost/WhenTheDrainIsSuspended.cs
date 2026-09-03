using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
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

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingStandaloneCosmosOutboxRelayHost
{
    /// <summary>
    /// The STANDALONE relay host's wiring of the #416 Drain Suspension: it consults the <see cref="OutboxDrainGate"/>
    /// ONCE PER BATCH for the Lease Token the batch arrived on — the one thing only the host knows — and a suspended
    /// lease throws, taking the module's EXISTING fail-closed exit (no checkpoint, the batch re-surfaces) rather than
    /// republishing a document whose confirmations keep failing.
    /// </summary>
    /// <remarks>
    /// It HALTS NOTHING: no <c>ChangeFeedProcessor</c> is stopped and no hosted service is stopped. Every assertion
    /// here lands on the non-emulator unit path, driving the host's change-feed handler directly.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenTheDrainIsSuspended
    {
        private const string InfrastructureType = "test-infra";
        private const string LeaseToken = "lease-a";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        /// <summary>The delivered stamp fault the failing container raises; a distinct type from the suspension throw.</summary>
        private static readonly TimeoutException _stampFailure = new TimeoutException("the delivered stamp timed out");

        /// <summary>
        /// A Confirmation Failure is reported to the gate against its lease, and the host rethrows the INNER fault:
        /// the internal carrier never escapes the module, so the error type on the shipped drain-failure count is
        /// unchanged.
        /// </summary>
        [Fact]
        public async Task MustRethrowTheInnerConfirmationFaultRatherThanTheCarrier()
        {
            var (provider, published) = RecordingProvider();
            StandaloneCosmosOutboxRelayHostedService host = Host(provider);
            OutboxDrainGate drainGate = ProcessorGate();

            Func<Task> act = () => Drain(host, FailingContainer(), drainGate, LeaseToken, PendingOutboxDocument("msg-1"));

            (await act.Should().ThrowAsync<TimeoutException>(
                "the host unwraps the Confirmation Failure carrier and rethrows the original fault with its original stack"))
                .Which.Should().BeSameAs(_stampFailure);
            published.Should().ContainSingle("the Confirmation Failure is reachable only after a successful publish");
        }

        /// <summary>
        /// Once a lease's consecutive Confirmation Failures reach the threshold, the next batch on it publishes
        /// NOTHING and throws, so the SDK does not checkpoint and the batch re-surfaces — the identical exit a publish
        /// failure already takes.
        /// </summary>
        [Fact]
        public async Task MustPublishNothingOnceTheLeaseIsSuspended()
        {
            var (provider, published) = RecordingProvider();
            StandaloneCosmosOutboxRelayHostedService host = Host(provider);
            OutboxDrainGate drainGate = ProcessorGate();
            Container container = FailingContainer();

            await FailConfirmations(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);

            Func<Task> act = () => Drain(host, container, drainGate, LeaseToken, PendingOutboxDocument("msg-1"));

            await act.Should().ThrowAsync<InvalidOperationException>(
                "a suspended lease takes the module's fail-closed exit so the batch is not checkpointed");
            published.Should().HaveCount(OutboxDrainGate.Threshold,
                "the suspended batch republished nothing, which is the amplification this closes");
        }

        /// <summary>
        /// The consult sits ABOVE the per-document pending-outbox pre-gate: a suspended lease throws even for a batch
        /// of documents the pre-gate would have skipped, which a consult placed below it could never do.
        /// </summary>
        [Fact]
        public async Task MustRefuseASuspendedLeaseAboveThePerDocumentPreGate()
        {
            var (provider, _) = RecordingProvider();
            StandaloneCosmosOutboxRelayHostedService host = Host(provider);
            OutboxDrainGate drainGate = ProcessorGate();
            Container container = FailingContainer();

            await FailConfirmations(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);

            Func<Task> act = () => Drain(host, container, drainGate, LeaseToken, DomainDocument());

            await act.Should().ThrowAsync<InvalidOperationException>(
                "the batch is refused for its lease, not for what its documents turned out to be");
        }

        /// <summary>
        /// A suspended lease opens NO DI scope and invokes NO body-resolver factory, so a suspension costs neither
        /// user code nor a scoped dependency graph.
        /// </summary>
        [Fact]
        public async Task MustNotInvokeTheBodyResolverFactoryWhileSuspended()
        {
            var (provider, _) = RecordingProvider();
            var factoryInvocations = 0;
            StandaloneCosmosOutboxRelayHostedService host = Host(provider, serviceProvider =>
            {
                factoryInvocations++;
                return ResolverPublishing();
            });
            OutboxDrainGate drainGate = ProcessorGate();
            Container container = FailingContainer();

            await FailConfirmations(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);
            int invocationsBeforeSuspension = factoryInvocations;

            Func<Task> act = () => Drain(host, container, drainGate, LeaseToken, PendingOutboxDocument("msg-1"));

            await act.Should().ThrowAsync<InvalidOperationException>();
            factoryInvocations.Should().Be(invocationsBeforeSuspension,
                "a suspended lease opens no scope, so the configured factory is never invoked");
        }

        /// <summary>
        /// The suspension is raised against ONE Lease Token: a sibling lease this host owns keeps draining, because
        /// its confirmations are landing.
        /// </summary>
        [Fact]
        public async Task MustKeepDrainingASiblingLease()
        {
            var (provider, published) = RecordingProvider();
            StandaloneCosmosOutboxRelayHostedService host = Host(provider);
            OutboxDrainGate drainGate = ProcessorGate();
            Container container = FailingContainer();

            await FailConfirmations(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);

            Func<Task> act = () => Drain(host, container, drainGate, "lease-b", PendingOutboxDocument("msg-1"));

            (await act.Should().ThrowAsync<TimeoutException>(
                "a lease that has not reached the threshold still drains and still reports its own stamp fault"))
                .Which.Should().BeSameAs(_stampFailure);
            published.Should().HaveCount(OutboxDrainGate.Threshold + 1, "the sibling lease published its document");
        }

        /// <summary>
        /// A confirmation that SUCCEEDS clears the lease, so the failures before it never accumulate into a
        /// suspension the confirmation path has already recovered from.
        /// </summary>
        [Fact]
        public async Task MustResumeCountingAfterAConfirmationSucceeds()
        {
            var (provider, published) = RecordingProvider();
            StandaloneCosmosOutboxRelayHostedService host = Host(provider);
            OutboxDrainGate drainGate = ProcessorGate();
            Container failingContainer = FailingContainer();

            await FailConfirmations(host, failingContainer, drainGate, LeaseToken, OutboxDrainGate.Threshold - 1);
            await Drain(host, ConfirmingContainer(), drainGate, LeaseToken, PendingOutboxDocument("msg-confirmed"));
            await FailConfirmations(host, failingContainer, drainGate, LeaseToken, OutboxDrainGate.Threshold - 1);

            Func<Task> act = () => Drain(host, failingContainer, drainGate, LeaseToken, PendingOutboxDocument("msg-1"));

            await act.Should().ThrowAsync<TimeoutException>(
                "the successful confirmation reset the consecutive count, so the lease is not suspended");
            published.Should().HaveCount((OutboxDrainGate.Threshold - 1) * 2 + 2, "every batch published its document");
        }

        /// <summary>
        /// An EMPTY batch confirms nothing, so it must not clear the lease. Absence of a Confirmation Failure is not
        /// presence of a confirmation: the loop reaches its end having performed no status write at all.
        /// </summary>
        /// <remarks>
        /// Clearing the lease here would reset the consecutive count as well, so the lease would need a full fresh
        /// threshold of failures to suspend again — the #416 bound degraded from one republish per window to five.
        /// </remarks>
        [Fact]
        public async Task MustNotClearTheLeaseForAnEmptyBatch()
        {
            var (provider, published) = RecordingProvider();
            StandaloneCosmosOutboxRelayHostedService host = Host(provider);
            OutboxDrainGate drainGate = ProcessorGate();
            Container container = FailingContainer();

            await FailConfirmations(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold - 1);
            await Drain(host, container, drainGate, LeaseToken);
            await FailConfirmations(host, container, drainGate, LeaseToken, 1);
            published.Clear();

            Func<Task> act = () => Drain(host, container, drainGate, LeaseToken, PendingOutboxDocument("msg-1"));

            await act.Should().ThrowAsync<InvalidOperationException>(
                "a batch that wrote nothing is evidence of nothing, so the failures before it still count toward the threshold");
            published.Should().BeEmpty("the suspended batch republished nothing");
        }

        /// <summary>
        /// A batch whose documents the pending-outbox pre-gate rejected confirms nothing either. The monitored
        /// container is CO-RESIDENT by design — domain writes, inbox markers and already-delivered documents all
        /// surface on this feed — so this is the ORDINARY batch, not an exotic one.
        /// </summary>
        [Fact]
        public async Task MustNotClearTheLeaseForABatchOfOnlyNonPendingDocuments()
        {
            var (provider, published) = RecordingProvider();
            StandaloneCosmosOutboxRelayHostedService host = Host(provider);
            OutboxDrainGate drainGate = ProcessorGate();
            Container container = FailingContainer();

            await FailConfirmations(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold - 1);
            await Drain(host, container, drainGate, LeaseToken, DomainDocument());
            await FailConfirmations(host, container, drainGate, LeaseToken, 1);
            published.Clear();

            Func<Task> act = () => Drain(host, container, drainGate, LeaseToken, PendingOutboxDocument("msg-1"));

            await act.Should().ThrowAsync<InvalidOperationException>(
                "a document the pre-gate skipped costs no status write, so it confirms nothing about the confirmation path");
            published.Should().BeEmpty("the suspended batch republished nothing");
        }

        /// <summary>
        /// The half-open probe: the window elapsed, exactly one batch was let through, and it confirmed nothing. The
        /// lease stays suspended for a fresh window, and the probe is spent.
        /// </summary>
        [Fact]
        public async Task MustLeaveTheLeaseSuspendedWhenTheProbeBatchConfirmsNothing()
        {
            var (provider, published) = RecordingProvider();
            StandaloneCosmosOutboxRelayHostedService host = Host(provider);
            var timeProvider = new AdvanceableTimeProvider();
            OutboxDrainGate drainGate = ProcessorGate(timeProvider);
            Container container = FailingContainer();

            await FailConfirmations(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);
            timeProvider.Advance(OutboxDrainGate.SuspensionWindow);
            await Drain(host, container, drainGate, LeaseToken);
            published.Clear();

            Func<Task> act = () => Drain(host, container, drainGate, LeaseToken, PendingOutboxDocument("msg-1"));

            await act.Should().ThrowAsync<InvalidOperationException>(
                "the probe discovered nothing about the confirmation path, so it lifts no suspension and is consumed");
            published.Should().BeEmpty("the still-suspended lease republished nothing");
        }

        /// <summary>
        /// A suspended batch is still counted against its lease. The batch count measures LEASE PROGRESS, so dropping
        /// it would make a stalled lease indistinguishable from an idle one — exactly the confusion the suspension
        /// exists to resolve.
        /// </summary>
        [Fact]
        public async Task MustStillRecordTheBatchAgainstASuspendedLease()
        {
            var (provider, _) = RecordingProvider();
            StandaloneCosmosOutboxRelayHostedService host = Host(provider);
            OutboxDrainGate drainGate = ProcessorGate();
            Container container = FailingContainer();

            await FailConfirmations(host, container, drainGate, LeaseToken, OutboxDrainGate.Threshold);

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                Func<Task> act = () => Drain(host, container, drainGate, LeaseToken, PendingOutboxDocument("msg-1"));

                await act.Should().ThrowAsync<InvalidOperationException>();

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedBatchesInstrumentName)
                          .Should().ContainSingle("the batch a suspended lease refused is still a batch that arrived");
            }
        }

        // Drives the supplied number of consecutive Confirmation Failures through the host, each its own batch, which
        // is how a lease reaches the threshold in production: the same document re-surfaces on every pass.
        private static async Task FailConfirmations(StandaloneCosmosOutboxRelayHostedService host, Container container, OutboxDrainGate drainGate, string leaseToken, int count)
        {
            for (int failure = 0; failure < count; failure++)
            {
                Func<Task> act = () => Drain(host, container, drainGate, leaseToken, PendingOutboxDocument("msg-1"));
                await act.Should().ThrowAsync<TimeoutException>();
            }
        }

        // The gate ONE processor drains through, standing in for the one BuildChangeFeedHandler constructs per
        // descriptor. No host owns a gate any more, so the handler is handed one, and the gate carries the Change-Feed
        // Source Identity its suspensions and batches are reported under.
        private static OutboxDrainGate ProcessorGate()
            => new OutboxDrainGate("chatter-cosmos-outbox-relay:source-under-test", new GuardedRelayLog(logger: null));

        // The same gate on a clock a test moves, which is how the half-open probe is reached with no wall-clock wait.
        private static OutboxDrainGate ProcessorGate(TimeProvider timeProvider)
            => new OutboxDrainGate("chatter-cosmos-outbox-relay:source-under-test", new GuardedRelayLog(logger: null), timeProvider);

        private static Task Drain(StandaloneCosmosOutboxRelayHostedService host, Container container, OutboxDrainGate drainGate, string leaseToken, params JsonObject[] documents)
            => host.HandleChangesAsync(BatchOf(documents), container, PartitionKeyPath, leaseToken, drainGate, CancellationToken.None);

        private static Stream BatchOf(params JsonObject[] documents)
        {
            var batch = new JsonObject { ["Documents"] = new JsonArray(documents) };
            return new MemoryStream(Encoding.UTF8.GetBytes(batch.ToJsonString()));
        }

        // The exact outbox wire document the Document-Tier Batch-Lifecycle Behavior writes, built fresh per call so
        // each batch owns its own nodes.
        private static JsonObject PendingOutboxDocument(string messageId)
        {
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InfrastructureType,
            };
            var outbound = new OutboundBrokeredMessage(messageId, new { OrderId = 7 }, messageContext, "orders", new JsonBodyConverter());

            return CosmosOutboxDocument.From(outbound).ToJsonObject(PartitionKeyPath, new List<JsonElement> { JsonValue("tenant-1") });
        }

        // A co-resident domain write: not a pending outbox document, so the per-document pre-gate skips it.
        private static JsonObject DomainDocument()
            => new JsonObject
            {
                ["id"] = "order-1",
                ["tenantId"] = "tenant-1",
            };

        private static JsonElement JsonValue(string raw)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(raw));
            return document.RootElement.Clone();
        }

        private static StandaloneCosmosOutboxRelayHostedService Host(IMessagingInfrastructureProvider provider,
                                                                    Func<IServiceProvider, IOutboxBodyResolver> bodyResolverFactory = null)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                provider,
                BodyConverterFactory(),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => Mock.Of<Container>(),
                    LeaseContainerFactory = _ => Mock.Of<Container>(),
                    PartitionKeyPath = PartitionKeyPath,
                    BodyResolverFactory = bodyResolverFactory,
                });

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
                      .Callback<OutboundBrokeredMessage, TransactionContext>((message, _) => published.Add(message))
                      .Returns(Task.CompletedTask);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return (provider.Object, published);
        }

        // A container that is not accepting the delivered stamp: the publish lands and the confirmation does not.
        private static Container FailingContainer()
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(_stampFailure);
            return container.Object;
        }

        // A container whose delivered stamp lands, which is what a recovered confirmation path looks like.
        private static Container ConfirmingContainer()
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(Mock.Of<ItemResponse<JsonElement>>());
            return container.Object;
        }

        // A resolver that owns the message for the configured-factory path.
        private static IOutboxBodyResolver ResolverPublishing()
        {
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InfrastructureType,
            };
            var resolved = new OutboundBrokeredMessage("msg-1", new { OrderId = 7 }, messageContext, "orders", new JsonBodyConverter());

            var resolver = new Mock<IOutboxBodyResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<OutboxDrainContext>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(resolved);
            return resolver.Object;
        }

        /// <summary>
        /// A <see cref="TimeProvider"/> whose monotonic timestamp only moves when a test moves it, so the suspension
        /// window is exercised with no wall-clock sleep.
        /// </summary>
        private sealed class AdvanceableTimeProvider : TimeProvider
        {
            private long _timestamp;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override long GetTimestamp() => _timestamp;

            public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        }
    }
}
