using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelayHost
{
    /// <summary>
    /// Characterizes pass 2 of the document-tier relay host's two-pass start: building and starting one Change-Feed
    /// Processor per descriptor. The generic host never invokes StopAsync on a hosted service whose StartAsync threw,
    /// so a start failure on descriptor N must stop EVERY processor the host already owns — including the in-flight one
    /// whose own start threw — rather than leaving them running for the lifetime of the process. Stopping is
    /// BEST-EFFORT: a stop that itself throws is logged and swallowed so it can never mask the start failure.
    /// </summary>
    public class WhenStartingProcessorsFails
    {
        private const string DatabaseId = "shop";
        private static readonly IReadOnlyList<string> DeclaredPartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        private sealed class CreateOrder : ICommand { }
        private sealed class ShipOrder : ICommand { }
        private sealed class CancelOrder : ICommand { }

        // The SDK's ChangeFeedProcessorBuilder is SEALED and unmockable, so the host exposes an internal processor
        // factory seam; ChangeFeedProcessor itself is public abstract with a public parameterless constructor, so the
        // thing the seam yields IS mockable.
        private static Mock<ChangeFeedProcessor> StartableProcessor()
        {
            var processor = new Mock<ChangeFeedProcessor>();
            processor.Setup(p => p.StartAsync()).Returns(Task.CompletedTask);
            processor.Setup(p => p.StopAsync()).Returns(Task.CompletedTask);
            return processor;
        }

        // A monitored container whose ground truth MATCHES its declared configuration, so pass 1 verification passes and
        // the test exercises pass 2. Mirrors the UsingMonitoredContainerContract harness: ContainerProperties exposes
        // PartitionKeyPaths only through its constructors and ContainerResponse ships a mocking constructor.
        private static Container VerifiablyConfiguredContainer(string containerId)
        {
            var properties = new ContainerProperties(containerId, DeclaredPartitionKeyPath)
            {
                DefaultTimeToLive = -1,
            };

            var response = new Mock<ContainerResponse>();
            response.SetupGet(r => r.Resource).Returns(properties);

            var database = new Mock<Database>();
            database.SetupGet(d => d.Id).Returns(DatabaseId);

            var container = new Mock<Container>();
            container.SetupGet(c => c.Id).Returns(containerId);
            container.SetupGet(c => c.Database).Returns(database.Object);
            container.Setup(c => c.ReadContainerAsync(It.IsAny<ContainerRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Returns(() => Task.FromResult(response.Object));
            return container.Object;
        }

        // The declared-source-identity (advanced) registration path keeps the host off the resolved handle's account
        // endpoint, so each registration is its own distinct Change-Feed Source Identity and gets its own descriptor.
        private static DocumentReliabilityRegistration RegistrationFor(Type commandType, string declaredSourceIdentity)
        {
            Container monitoredContainer = VerifiablyConfiguredContainer(declaredSourceIdentity + "-container");
            Container leaseContainer = Mock.Of<Container>();
            return new DocumentReliabilityRegistration(
                commandType,
                DatabaseId,
                declaredSourceIdentity + ":document",
                declaredSourceIdentity + ":lease",
                _ => new PartitionKey("pk"),
                DeclaredPartitionKeyPath,
                documentContainerFactory: _ => monitoredContainer,
                leaseContainerFactory: _ => leaseContainer,
                declaredSourceIdentity: new CosmosSourceIdentity(declaredSourceIdentity, declaredSourceIdentity + "-lease"));
        }

        // One registration (and so one descriptor) per supplied processor, with the host's processor factory seam
        // handing them out in descriptor order.
        private static CosmosOutboxRelayHostedService HostWith(ILogger<CosmosOutboxRelayHostedService> logger,
                                                               params Mock<ChangeFeedProcessor>[] processors)
        {
            Type[] commandTypes = new[] { typeof(CreateOrder), typeof(ShipOrder), typeof(CancelOrder) };

            var registry = new DocumentReliabilityRegistry();
            for (int index = 0; index < processors.Length; index++)
            {
                // Add is internal; InternalsVisibleTo exposes it to the test assembly.
                registry.Add(RegistrationFor(commandTypes[index], "source-" + index));
            }

            var services = new ServiceCollection();
            services.AddSingleton(new Mock<CosmosClient>(MockBehavior.Strict).Object);

            var host = new CosmosOutboxRelayHostedService(
                registry,
                new CosmosContainerFactory(services.BuildServiceProvider()),
                Mock.Of<IMessagingInfrastructureProvider>(),
                Mock.Of<IBodyConverterFactory>(),
                logger);

            var pending = new Queue<ChangeFeedProcessor>(processors.Select(processor => processor.Object));
            host.ProcessorFactory = (descriptor, instanceName, onChanges) => pending.Dequeue();
            return host;
        }

        private static void ShouldHaveBeenStoppedOnce(Mock<ChangeFeedProcessor> processor, string because)
            => processor.Verify(p => p.StopAsync(), Times.Once, because);

        /// <summary>
        /// The ownership PIN. A processor is tracked BEFORE its start is awaited, so the in-flight processor whose own
        /// start threw is stopped alongside the ones already running, the tracking list is emptied, and the ORIGINAL
        /// start failure is what the caller observes.
        /// </summary>
        [Fact]
        public async Task MustStopEveryTrackedProcessorAndSurfaceTheOriginalFailure()
        {
            var startFailure = new InvalidOperationException("the third change-feed processor could not start");
            Mock<ChangeFeedProcessor> first = StartableProcessor();
            Mock<ChangeFeedProcessor> second = StartableProcessor();
            Mock<ChangeFeedProcessor> inFlight = StartableProcessor();
            inFlight.Setup(p => p.StartAsync()).ThrowsAsync(startFailure);

            CosmosOutboxRelayHostedService host = HostWith(logger: null, first, second, inFlight);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "a change-feed processor that cannot start must take the host down at start"))
                .Which.Should().BeSameAs(startFailure, "cleanup may never replace the failure that caused it");

            ShouldHaveBeenStoppedOnce(first, "an already-running processor must not leak when a later start fails");
            ShouldHaveBeenStoppedOnce(second, "an already-running processor must not leak when a later start fails");
            ShouldHaveBeenStoppedOnce(inFlight, "the in-flight processor is tracked before its start is awaited, so it is stopped too");
            host.TrackedProcessors.Should().BeEmpty("the host owns no processor once the start-failure cleanup has run");
        }

        /// <summary>
        /// Cleanup is BEST-EFFORT: the SDK throws when stopping a processor that never finished starting. Such a stop
        /// failure is swallowed so it can never become the failure the host reports — but it is still reported at Error
        /// through the host's own logger rather than silently discarded.
        /// </summary>
        [Fact]
        public async Task MustLogAndSwallowACleanupStopFailureRatherThanMaskTheStartFailure()
        {
            var startFailure = new InvalidOperationException("the second change-feed processor could not start");
            var stopFailure = new InvalidOperationException("a processor that never finished starting cannot be stopped");
            Mock<ChangeFeedProcessor> running = StartableProcessor();
            running.Setup(p => p.StopAsync()).ThrowsAsync(stopFailure);
            Mock<ChangeFeedProcessor> inFlight = StartableProcessor();
            inFlight.Setup(p => p.StartAsync()).ThrowsAsync(startFailure);

            var logger = new RecordingLogger();
            CosmosOutboxRelayHostedService host = HostWith(logger, running, inFlight);

            Func<Task> start = () => host.StartAsync(CancellationToken.None);

            (await start.Should().ThrowAsync<InvalidOperationException>(
                "the start failure is what takes the host down"))
                .Which.Should().BeSameAs(startFailure, "a cleanup that itself fails may never replace the failure it is cleaning up after");

            ShouldHaveBeenStoppedOnce(inFlight, "one processor refusing to stop must not abandon the processors behind it");
            host.TrackedProcessors.Should().BeEmpty("the host owns no processor once the start-failure cleanup has run");

            var entry = logger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(LogLevel.Error);
            entry.Exception.Should().BeSameAs(stopFailure, "a swallowed cleanup failure is still reported, at Error, rather than discarded");
        }

        /// <summary>
        /// The happy path: every processor the host started is one it owns, and shutdown stops all of them and leaves
        /// the host owning none.
        /// </summary>
        [Fact]
        public async Task MustTrackEveryStartedProcessorAndStopThemAllOnShutdown()
        {
            Mock<ChangeFeedProcessor> first = StartableProcessor();
            Mock<ChangeFeedProcessor> second = StartableProcessor();
            CosmosOutboxRelayHostedService host = HostWith(logger: null, first, second);

            await host.StartAsync(CancellationToken.None);

            host.TrackedProcessors.Should().HaveCount(2, "the host owns every processor it started");
            first.Verify(p => p.StartAsync(), Times.Once);
            second.Verify(p => p.StartAsync(), Times.Once);

            await host.StopAsync(CancellationToken.None);

            ShouldHaveBeenStoppedOnce(first, "shutdown stops every processor the host owns");
            ShouldHaveBeenStoppedOnce(second, "shutdown stops every processor the host owns");
            host.TrackedProcessors.Should().BeEmpty("the host owns no processor after shutdown");
        }

        /// <summary>
        /// Shutdown attempts EVERY processor even when an earlier stop throws, and then surfaces the stop failure —
        /// unlike the start-failure cleanup, which swallows it.
        /// </summary>
        [Fact]
        public async Task MustStopEveryProcessorOnShutdownWhenAnEarlierStopThrows()
        {
            var stopFailure = new InvalidOperationException("the first change-feed processor could not stop");
            Mock<ChangeFeedProcessor> first = StartableProcessor();
            first.Setup(p => p.StopAsync()).ThrowsAsync(stopFailure);
            Mock<ChangeFeedProcessor> second = StartableProcessor();
            Mock<ChangeFeedProcessor> third = StartableProcessor();

            CosmosOutboxRelayHostedService host = HostWith(logger: null, first, second, third);
            await host.StartAsync(CancellationToken.None);

            Func<Task> stop = () => host.StopAsync(CancellationToken.None);

            (await stop.Should().ThrowAsync<InvalidOperationException>(
                "a shutdown that could not stop a processor is the caller's business, so it is surfaced rather than swallowed"))
                .Which.Should().BeSameAs(stopFailure);

            ShouldHaveBeenStoppedOnce(second, "a processor that refuses to stop must not abandon the processors behind it");
            ShouldHaveBeenStoppedOnce(third, "a processor that refuses to stop must not abandon the processors behind it");
            host.TrackedProcessors.Should().BeEmpty("the host owns no processor after shutdown, even a failed one");
        }

        /// <summary>
        /// A host with no document-tier registrations owns nothing, so the start-failure cleanup path has nothing to
        /// clean up and shutdown is a no-op.
        /// </summary>
        [Fact]
        public async Task MustOwnNothingToCleanUpForAnEmptyRegistry()
        {
            CosmosOutboxRelayHostedService host = HostWith(logger: null);

            await host.StartAsync(CancellationToken.None);

            host.TrackedProcessors.Should().BeEmpty("a host with no registrations builds no processor to own");

            Func<Task> stop = () => host.StopAsync(CancellationToken.None);

            await stop.Should().NotThrowAsync("stopping a host that owns no processor collects no failure to surface");
        }

        /// <summary>An <see cref="ILogger{TCategoryName}"/> recorder that captures each log call the host makes.</summary>
        private sealed class RecordingLogger : ILogger<CosmosOutboxRelayHostedService>
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
