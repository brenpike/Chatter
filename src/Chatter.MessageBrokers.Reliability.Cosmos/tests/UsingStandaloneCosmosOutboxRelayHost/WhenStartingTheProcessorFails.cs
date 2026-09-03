using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingStandaloneCosmosOutboxRelayHost
{
    /// <summary>
    /// Characterizes the STANDALONE relay host's ownership of the single change-feed processor it builds. The generic
    /// host never invokes StopAsync on a hosted service whose StartAsync threw, so the host must own the processor
    /// BEFORE it awaits the start — otherwise a start that throws leaves the built processor unreferenced, with nothing
    /// able to stop it. Stopping such a processor is BEST-EFFORT: the SDK throws when stopping one that never finished
    /// starting, and that stop failure is logged at Error and swallowed so it can never mask the start failure.
    /// </summary>
    public class WhenStartingTheProcessorFails
    {
        private const string DatabaseId = "shop";
        private const string MonitoredContainerId = "orders";
        private const string LeaseContainerId = "orders-leases";

        private static readonly IReadOnlyList<string> DeclaredPartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        // The SDK's ChangeFeedProcessorBuilder is SEALED (and its fluent methods non-virtual), so the build chain is
        // unmockable and the host exposes an internal processor-factory seam; ChangeFeedProcessor itself is public
        // abstract with a public parameterless constructor, so the thing the seam yields IS mockable.
        private static Mock<ChangeFeedProcessor> StartableProcessor()
        {
            var processor = new Mock<ChangeFeedProcessor>();
            processor.Setup(p => p.StartAsync()).Returns(Task.CompletedTask);
            processor.Setup(p => p.StopAsync()).Returns(Task.CompletedTask);
            return processor;
        }

        // A container whose resolved physical identity (.Id + .Database.Id) and account endpoint
        // (.Database.Client.Endpoint) are fixed, so the ground-truth source-identity key resolves without a live SDK.
        private static Mock<Container> PhysicalContainer(string databaseId, string containerId)
        {
            var client = new Mock<CosmosClient>();
            client.SetupGet(c => c.Endpoint).Returns(new Uri("https://acct.documents.azure.com/"));

            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns(databaseId);
            database.SetupGet(d => d.Client).Returns(client.Object);

            var container = new Mock<Container>();
            container.SetupGet(c => c.Id).Returns(containerId);
            container.SetupGet(c => c.Database).Returns(database.Object);
            return container;
        }

        // A monitored container whose ground truth MATCHES its declared configuration, so start-time verification
        // passes and the test exercises the build-and-start step behind it.
        private static Container VerifiablyConfiguredContainer()
        {
            var properties = new ContainerProperties(MonitoredContainerId, DeclaredPartitionKeyPath)
            {
                DefaultTimeToLive = -1,
            };

            var response = new Mock<ContainerResponse>();
            response.SetupGet(r => r.Resource).Returns(properties);

            Mock<Container> container = PhysicalContainer(DatabaseId, MonitoredContainerId);
            container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(response.Object);
            return container.Object;
        }

        // A standalone host whose processor-factory seam hands out the supplied processor instead of running the real
        // (unmockable) builder chain.
        private static StandaloneCosmosOutboxRelayHostedService HostWith(Mock<ChangeFeedProcessor> processor,
                                                                        ILogger<StandaloneCosmosOutboxRelayHostedService> logger = null)
        {
            var host = new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => VerifiablyConfiguredContainer(),
                    LeaseContainerFactory = _ => PhysicalContainer(DatabaseId, LeaseContainerId).Object,
                    PartitionKeyPath = DeclaredPartitionKeyPath,
                },
                processorRegistry: null,
                logger: logger);

            host.ProcessorFactory = (descriptor, instanceName, onChanges) => processor.Object;
            return host;
        }

        /// <summary>
        /// The ownership PIN. The processor is owned BEFORE its start is awaited, so a start that throws still leaves a
        /// processor the host can stop, and the ORIGINAL start failure is what the caller observes.
        /// </summary>
        [Fact]
        public async Task MustStopTheProcessorAndSurfaceTheOriginalFailure()
        {
            var startFailure = new InvalidOperationException("the change-feed processor could not start");
            Mock<ChangeFeedProcessor> processor = StartableProcessor();
            processor.Setup(p => p.StartAsync()).ThrowsAsync(startFailure);

            StandaloneCosmosOutboxRelayHostedService host = HostWith(processor);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "a change-feed processor that cannot start must take the host down at start"))
                .Which.Should().BeSameAs(startFailure, "cleanup may never replace the failure that caused it");

            processor.Verify(p => p.StopAsync(), Times.Once,
                "the processor is owned before its start is awaited, so a failed start still stops it rather than abandoning it unreferenced");
        }

        /// <summary>
        /// The start-failure cleanup DISOWNS the processor, so the shutdown the caller may still run afterwards is a
        /// no-op rather than a second stop of an already-stopped processor.
        /// </summary>
        [Fact]
        public async Task MustNotStopTheProcessorAgainOnASubsequentShutdown()
        {
            var startFailure = new InvalidOperationException("the change-feed processor could not start");
            Mock<ChangeFeedProcessor> processor = StartableProcessor();
            processor.Setup(p => p.StartAsync()).ThrowsAsync(startFailure);

            StandaloneCosmosOutboxRelayHostedService host = HostWith(processor);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);
            await start.Should().ThrowAsync<InvalidOperationException>();

            Func<Task> stop = () => host.StopAsync(CancellationToken.None);

            await stop.Should().NotThrowAsync("a host that owns no processor has nothing to stop");
            processor.Verify(p => p.StopAsync(), Times.Once,
                "the start-failure cleanup disowned the processor, so shutdown does not stop it a second time");
        }

        /// <summary>
        /// Cleanup is BEST-EFFORT: the SDK throws when stopping a processor that never finished starting. Such a stop
        /// failure is swallowed so it can never become the failure the host reports — but it is still reported at Error
        /// through the host's own logger rather than silently discarded.
        /// </summary>
        [Fact]
        public async Task MustLogAndSwallowACleanupStopFailureRatherThanMaskTheStartFailure()
        {
            var startFailure = new InvalidOperationException("the change-feed processor could not start");
            var stopFailure = new InvalidOperationException("a processor that never finished starting cannot be stopped");
            Mock<ChangeFeedProcessor> processor = StartableProcessor();
            processor.Setup(p => p.StartAsync()).ThrowsAsync(startFailure);
            processor.Setup(p => p.StopAsync()).ThrowsAsync(stopFailure);

            var logger = new RecordingLogger();
            StandaloneCosmosOutboxRelayHostedService host = HostWith(processor, logger);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "the start failure is what takes the host down"))
                .Which.Should().BeSameAs(startFailure, "a cleanup that itself fails may never replace the failure it is cleaning up after");

            var entry = logger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(LogLevel.Error);
            entry.Exception.Should().BeSameAs(stopFailure, "a swallowed cleanup failure is still reported, at Error, rather than discarded");
        }

        /// <summary>
        /// The happy path: a processor that started is one the host owns, and shutdown stops it exactly once and leaves
        /// the host owning none.
        /// </summary>
        [Fact]
        public async Task MustStopTheStartedProcessorOnShutdownExactlyOnce()
        {
            Mock<ChangeFeedProcessor> processor = StartableProcessor();
            StandaloneCosmosOutboxRelayHostedService host = HostWith(processor);

            await host.StartAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);
            await host.StopAsync(CancellationToken.None);

            processor.Verify(p => p.StartAsync(), Times.Once);
            processor.Verify(p => p.StopAsync(), Times.Once,
                "shutdown stops the processor the host owns and then owns none, so a second shutdown is a no-op");
        }

        /// <summary>An <see cref="ILogger{TCategoryName}"/> recorder that captures each log call the host makes.</summary>
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
