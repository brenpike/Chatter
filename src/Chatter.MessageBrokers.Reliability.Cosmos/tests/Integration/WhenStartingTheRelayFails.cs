using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // The relay host's start-failure cleanup, proven against the REAL Cosmos SDK rather than a mocked
    // ChangeFeedProcessor. The unit suite (UsingCosmosOutboxRelayHost/WhenStartingProcessorsFails and
    // UsingStandaloneCosmosOutboxRelayHost/WhenStartingTheProcessorFails) already owns every assertion that must
    // protect CI — the ownership pin, the disown, the swallow-and-log, the type-preserving rethrow — all against a
    // Mock<ChangeFeedProcessor>. What only the live SDK can add is whether the two ASSUMPTIONS those fakes are built
    // on are true of the real thing:
    //   1. stopping a processor that never successfully started THROWS, which is the whole reason the start-failure
    //      cleanup swallows-and-logs its stop failure instead of surfacing it, and
    //   2. a genuine SDK-side start failure travels out of the host unchanged, with the swallowed cleanup failure
    //      logged rather than substituted for it.
    //
    // Each test provisions its OWN monitored container and declares a UNIQUE source-identity pair so its processor
    // name never joins another relay's consumer group in the shared collection.
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenStartingTheRelayFails
    {
        // The monitored container of the raw-SDK probe: contract-satisfying, so nothing but the missing start can
        // explain a stop failure.
        private const string NeverStartedContainerName = "start-failure-never-started";
        // The monitored container of the host-driven start failure. Contract-satisfying for the same reason: the
        // host must get PAST pass-1 verification so the failure under test is the SDK's, not the contract's.
        private const string MonitoredContainerName = "start-failure-monitored";
        // A lease container that is NEVER provisioned. Handing the relay a handle to it is how a genuine SDK-side
        // start failure is induced without touching the processor-factory seam: the change-feed processor's own
        // lease-store initialization is what 404s.
        private const string MissingLeaseContainerName = "start-failure-absent-leases";

        private const int NonPurgingDefaultTimeToLive = -1;

        private static readonly IReadOnlyList<string> DeclaredPartitionKeyPath = Array.AsReadOnly(new[] { CosmosTestClient.PartitionKeyPath });

        private readonly CosmosEmulatorFixture _emulator;

        public WhenStartingTheRelayFails(CosmosEmulatorFixture emulator) => _emulator = emulator;

        // THE ASSUMPTION THE PRODUCTION SWALLOW RESTS ON, asked of the RAW SDK with no host and no seam in the way:
        // a real ChangeFeedProcessor built off a real container and never started does NOT stop quietly. If it did,
        // the start-failure cleanup would have no failure to absorb and could surface a stop failure instead of
        // logging it away.
        [RequiresDockerFact]
        public async Task StoppingAProcessorThatNeverStartedThrowsFromTheRealSdk()
        {
            await using CosmosTestClient testClient = await CreateTestClientAsync();
            Container monitored = await testClient.CreateContainerWithDefaultTimeToLiveAsync(NeverStartedContainerName, NonPurgingDefaultTimeToLive);

            ChangeFeedProcessor processor = BuildNeverStartedProcessor(monitored, LeaseContainer(testClient));

            Func<Task> stop = () => processor.StopAsync();

            // Asserted at the granularity the host's cleanup actually catches (Exception), not at a specific type: the
            // SDK does not document WHAT it throws here, and observed against 3.61.0 it is a NullReferenceException out
            // of ChangeFeedProcessorCore.StopAsync rather than the InvalidOperationException its own
            // "Start has to be called before stop." message would suggest. What the production cleanup depends on is
            // only that stopping FAILS rather than succeeding quietly, so that is what is pinned — and a future SDK that
            // stopped throwing would still fail this test.
            await stop.Should().ThrowAsync<Exception>(
                "the SDK refuses to stop a processor whose start never completed — which is exactly why the host's " +
                "start-failure cleanup treats stopping as best-effort and swallows the stop failure rather than " +
                "letting it replace the start failure");
        }

        // The cleanup contract end to end over REAL containers, with the start failure induced by the SDK itself (an
        // absent lease container) rather than by the processor-factory seam. Three things must hold at once: the
        // ORIGINAL SDK failure is what the caller sees, the swallowed cleanup stop failure is reported at Error
        // instead of discarded, and the host owns no processor afterwards.
        [RequiresDockerFact]
        public async Task SurfacesTheRealStartFailureAndOwnsNoProcessorAfterTheCleanup()
        {
            await using CosmosTestClient testClient = await CreateTestClientAsync();
            Container monitored = await testClient.CreateContainerWithDefaultTimeToLiveAsync(MonitoredContainerName, NonPurgingDefaultTimeToLive);
            Container absentLease = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, MissingLeaseContainerName);

            var logger = new RecordingLogger();
            StandaloneCosmosOutboxRelayHostedService host = RelayHost(monitored, absentLease, logger);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            CosmosException startFailure = (await start.Should().ThrowAsync<CosmosException>(
                "a change-feed processor whose lease container does not exist cannot start, and that SDK failure must " +
                "take the host down rather than leave a half-started relay running"))
                .Which;
            startFailure.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "the surfaced failure is the SDK's own lease-store 404, not an exception the cleanup manufactured");

            logger.Entries.Should().NotBeEmpty(
                "the real SDK refuses to stop the processor whose start just failed, and that swallowed cleanup " +
                "failure is reported rather than discarded");
            logger.Entries.Should().OnlyContain(entry => entry.Level == LogLevel.Error && entry.Exception != null,
                "a swallowed cleanup failure is logged at Error, carrying the exception it swallowed");
            logger.Entries.Should().NotContain(entry => ReferenceEquals(entry.Exception, startFailure),
                "the start failure is surfaced to the caller, not logged away as if it were the cleanup's");

            Func<Task> stop = () => host.StopAsync(CancellationToken.None);

            await stop.Should().NotThrowAsync(
                "the cleanup disowned the processor, so the shutdown that may still follow a failed start has nothing " +
                "left to stop and no second stop failure to surface");
        }

        private Task<CosmosTestClient> CreateTestClientAsync()
            => CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);

        private static Container LeaseContainer(CosmosTestClient testClient)
            => testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.LeaseContainerName);

        // A real ChangeFeedProcessor over the supplied real containers, built through the SDK's own builder chain the
        // way the host builds one, and deliberately never started. The change handler is never invoked because the
        // processor never runs.
        private static ChangeFeedProcessor BuildNeverStartedProcessor(Container monitored, Container lease)
            => monitored
                .GetChangeFeedProcessorBuilder(UniqueToken("never-started"), (ChangeFeedProcessorContext _, Stream __, CancellationToken ___) => Task.CompletedTask)
                .WithInstanceName(UniqueToken("instance"))
                .WithLeaseContainer(lease)
                .Build();

        // A standalone relay host over the supplied REAL container handles. The declared source identities are unique
        // per host so the derived processor name never collides with another test's relay in the shared collection;
        // the publish collaborators are stubs because no document is ever drained here — the start fails first.
        private static StandaloneCosmosOutboxRelayHostedService RelayHost(Container monitored,
                                                                          Container lease,
                                                                          ILogger<StandaloneCosmosOutboxRelayHostedService> logger)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => monitored,
                    LeaseContainerFactory = _ => lease,
                    PartitionKeyPath = DeclaredPartitionKeyPath,
                    MonitoredSourceIdentity = UniqueToken("start-failure-monitored"),
                    LeaseSourceIdentity = UniqueToken("start-failure-lease"),
                },
                processorRegistry: null,
                logger: logger);

        private static string UniqueToken(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

        // An ILogger{TCategoryName} recorder that captures each log call the host makes, mirroring the recorder the
        // unit-path counterparts use.
        private sealed class RecordingLogger : ILogger<StandaloneCosmosOutboxRelayHostedService>
        {
            public List<(LogLevel Level, string Message, Exception Exception)> Entries { get; } = new List<(LogLevel, string, Exception)>();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception), exception));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();

                public void Dispose()
                {
                }
            }
        }
    }
}
