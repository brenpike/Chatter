using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosReliabilityDiagnostics
{
    /// <summary>
    /// The BATCH-TIER drain hop observed as LEASE PROGRESS: every change-feed batch a host is handed is sized and
    /// counted against the lease it was delivered for, so an idle partition stays distinguishable from a stalled one.
    /// </summary>
    /// <remarks>
    /// METRICS ONLY — this hop declares NO span, deliberately. A batch span would be the natural parent for the
    /// per-document send spans beneath it, and adopting it would re-sever the write-time trace the Outbox Document
    /// persisted (ADR-0010 D6): a batch is a change-feed DELIVERY, not the cause of the message. With no batch span
    /// there is nothing for a send span to be parented off, so a duplicate send span is UNREPRESENTABLE rather than
    /// merely unwritten. Were one ever added, the per-document send spans would have to attach it as a LINK.
    /// The two hosts are near-duplicates but not identical, so every behaviour below is asserted against BOTH.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenHandlingBatchesWithDiagnosticsEnabled : Testing.Core.Context
    {
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        /// <summary>The change-feed lease the batch under test was delivered for.</summary>
        private const string LeaseToken = "lease-3";

        private const string TenantId = "tenant-1";

        /// <summary>A meter name no Chatter instrument belongs to, standing in for another library's opt-in.</summary>
        private const string SentinelMeterName = "Contoso.Unrelated.Instrumentation";

        [Fact]
        public async Task MustSizeAndCountABatchAgainstItsLeaseOnTheRegistryHost()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            using (Stream batch = BatchOf(CoResidentDocument("a"), CoResidentDocument("b"), CoResidentDocument("c")))
            {
                await RegistryHost().HandleChangesAsync(batch, Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);

                AssertOneBatchRecorded(meterScope, expectedSize: 3);
            }
        }

        [Fact]
        public async Task MustSizeAndCountABatchAgainstItsLeaseOnTheStandaloneHost()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            using (Stream batch = BatchOf(CoResidentDocument("a"), CoResidentDocument("b"), CoResidentDocument("c")))
            {
                await StandaloneHost().HandleChangesAsync(batch, Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);

                AssertOneBatchRecorded(meterScope, expectedSize: 3);
            }
        }

        /// <summary>
        /// An EMPTY batch is still a batch: size zero, count one. Dropping it would turn the batch counter into a
        /// measure of change-feed ACTIVITY rather than LEASE PROGRESS, leaving an idle partition indistinguishable
        /// from a stalled one — the exact question this instrument exists to answer.
        /// </summary>
        [Fact]
        public async Task MustRecordAnEmptyBatchAsSizeZeroAndOneBatchOnTheRegistryHost()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            using (Stream batch = BatchOf())
            {
                await RegistryHost().HandleChangesAsync(batch, Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);

                AssertOneBatchRecorded(meterScope, expectedSize: 0);
            }
        }

        [Fact]
        public async Task MustRecordAnEmptyBatchAsSizeZeroAndOneBatchOnTheStandaloneHost()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            using (Stream batch = BatchOf())
            {
                await StandaloneHost().HandleChangesAsync(batch, Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);

                AssertOneBatchRecorded(meterScope, expectedSize: 0);
            }
        }

        /// <summary>
        /// A batch shape the relay cannot parse records NOTHING and still FAILS CLOSED. Both halves matter: the size
        /// is unknown, so reporting one would be a fabricated measurement, and normal handler completion is the SDK's
        /// checkpoint signal, so swallowing the throw to reach the emit site would advance the lease past documents
        /// that were never published.
        /// </summary>
        [Theory]
        [InlineData("{}")]
        [InlineData("{\"Documents\":\"not-an-array\"}")]
        [InlineData("{\"Documents\":{}}")]
        [InlineData("{\"documents\":[]}")]
        public async Task MustRecordNothingAndStillFailClosedOnAMalformedBatchPayloadOnTheRegistryHost(string payload)
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosOutboxRelayHostedService host = RegistryHost();

                Func<Task> act = () => host.HandleChangesAsync(StreamOf(payload), Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);

                await act.Should().ThrowAsync<InvalidOperationException>(
                    "an unparseable batch must fault so the SDK does not checkpoint the lease past unpublished documents");

                AssertNoBatchRecorded(meterScope);
            }
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"Documents\":\"not-an-array\"}")]
        [InlineData("{\"Documents\":{}}")]
        [InlineData("{\"documents\":[]}")]
        public async Task MustRecordNothingAndStillFailClosedOnAMalformedBatchPayloadOnTheStandaloneHost(string payload)
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                StandaloneCosmosOutboxRelayHostedService host = StandaloneHost();

                Func<Task> act = () => host.HandleChangesAsync(StreamOf(payload), Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);

                await act.Should().ThrowAsync<InvalidOperationException>(
                    "an unparseable batch must fault so the SDK does not checkpoint the lease past unpublished documents");

                AssertNoBatchRecorded(meterScope);
            }
        }

        /// <summary>
        /// The batch hop starts NO <see cref="System.Diagnostics.Activity"/>, on either host, with a .NET
        /// <c>ActivityListener</c> attached to this module's own scope and sampling everything in.
        /// </summary>
        /// <remarks>
        /// The meter scope is attached alongside so the assertion is not vacuous: the recorded measurements prove the
        /// batch hop actually ran, and the empty activity list is then a statement about a hop that executed rather
        /// than about one that never happened.
        /// </remarks>
        [Fact]
        public async Task MustStartNoSpanForADrainedBatchOnEitherHost()
        {
            using (var activityScope = new RecordingActivityScope(CosmosReliabilityDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            using (Stream registryBatch = BatchOf(CoResidentDocument("a")))
            using (Stream standaloneBatch = BatchOf(CoResidentDocument("a")))
            {
                await RegistryHost().HandleChangesAsync(registryBatch, Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);
                await StandaloneHost().HandleChangesAsync(standaloneBatch, Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedBatchesInstrumentName).Should().HaveCount(2,
                    "both hosts drained a batch, so the no-span assertion below is about a hop that ran");
                activityScope.StartedActivities.Should().BeEmpty(
                    "a batch span would become the natural parent of the per-document send spans and re-sever the write-time trace");
            }
        }

        /// <summary>
        /// An application that never subscribed to this module's meter drains exactly as it always did and publishes
        /// nothing, on either host.
        /// </summary>
        /// <remarks>
        /// The sentinel meter scope is the OBSERVER, not an opt-in: it matches <c>Instrument.Meter.Name</c> exactly,
        /// so no Chatter instrument is ever enabled through it and <see cref="CosmosReliabilityDiagnostics.IsEnabled"/>
        /// stays false. That is what makes this the off path — the emit site's own guard short-circuits, so the batch
        /// length is never read for a measurement nobody subscribed to (ADR-0010 R1).
        /// </remarks>
        [Fact]
        public async Task MustDrainAndPublishNothingWhenDiagnosticsAreNotOptedInto()
        {
            using (var meterScope = new RecordingMeterScope(SentinelMeterName))
            using (Stream registryBatch = BatchOf(CoResidentDocument("a")))
            using (Stream standaloneBatch = BatchOf(CoResidentDocument("a")))
            {
                CosmosReliabilityDiagnostics.IsEnabled.Should().BeFalse();

                await RegistryHost().HandleChangesAsync(registryBatch, Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);
                await StandaloneHost().HandleChangesAsync(standaloneBatch, Mock.Of<Container>(), PartitionKeyPath, LeaseToken, ProcessorGate(), CancellationToken.None);

                meterScope.Measurements.Should().BeEmpty("no instrument may publish to an application that never opted in");
            }
        }

        // One batch handed to one host records exactly one size and exactly one count, both carrying the lease the
        // batch was delivered for.
        private static void AssertOneBatchRecorded(RecordingMeterScope meterScope, int expectedSize)
        {
            var size = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainBatchSizeInstrumentName).Should().ContainSingle().Subject;
            size.Value.Should().Be(expectedSize);

            var count = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedBatchesInstrumentName).Should().ContainSingle().Subject;
            count.Value.Should().Be(1);

            foreach (var measurement in new[] { size, count })
            {
                measurement.TryGetTag(CosmosReliabilityDiagnostics.LeaseToken, out var leaseToken).Should().BeTrue();
                leaseToken.Should().Be(LeaseToken);
            }
        }

        private static void AssertNoBatchRecorded(RecordingMeterScope meterScope)
        {
            meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainBatchSizeInstrumentName).Should().BeEmpty();
            meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedBatchesInstrumentName).Should().BeEmpty();
        }

        // A change-feed batch payload in the wire shape the SDK hands the host; no argument is the empty batch.
        private static Stream BatchOf(params string[] documents)
            => StreamOf("{\"Documents\":[" + string.Join(",", documents) + "]}");

        private static Stream StreamOf(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

        // A co-resident document neither host admits: no Chatter discriminator, no pending status. The batch size is
        // the BATCH's document count, not the admitted count, so a batch of these still sizes as three.
        private static string CoResidentDocument(string id) => "{\"id\":\"" + id + "\",\"tenantId\":\"" + TenantId + "\"}";

        // The gate ONE processor drains through, standing in for the one BuildChangeFeedHandler constructs per
        // descriptor. No host owns a gate any more, so the handler is handed one.
        private static OutboxDrainGate ProcessorGate() => new OutboxDrainGate(new GuardedRelayLog(logger: null));

        private static CosmosOutboxRelayHostedService RegistryHost()
            => new CosmosOutboxRelayHostedService(
                new DocumentReliabilityRegistry(),
                new CosmosContainerFactory(Mock.Of<IServiceProvider>()),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>());

        private static StandaloneCosmosOutboxRelayHostedService StandaloneHost()
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => Mock.Of<Container>(),
                    LeaseContainerFactory = _ => Mock.Of<Container>(),
                    PartitionKeyPath = PartitionKeyPath,
                });
    }
}
