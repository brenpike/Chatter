#nullable disable

using Chatter.CQRS;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.DependencyInjection.UsingChatterMessageBrokerExtensions
{
    /// <summary>
    /// Pins the ownership of the DI scope the discovered Brokered Message Receiver graph is resolved from: the scope
    /// belongs to the hosted background service's RECEIVE LOOP, not to the service object. Nothing is acquired until
    /// the loop opens the scope, every scoped member stays alive for as long as the loop reads it, and the scope is
    /// released as the loop exits — by return, by throw, or by the cancellation of a host that never stopped it.
    ///
    /// INVARIANT: the probes below are registered BEFORE AddChatterCqrs/AddMessageBrokers, so the framework's
    /// AddIfNotRegistered defaults skip the already-present service type and the probe lands in the real receiver graph.
    ///
    /// INVARIANT: assembly scanning is scoped to a marker assembly containing NO [BrokeredMessage]-decorated
    /// <see cref="IMessage"/> types, so attribute-driven receiver discovery is deterministically empty and only the
    /// explicit AddReceiver route contributes receivers. This mirrors <see cref="WhenBootingHost"/>; the graph builder
    /// is deliberately duplicated rather than shared because this one needs a pre-registration hook that one lacks.
    /// </summary>
    public class WhenOwningTheReceiverScope : Testing.Core.Context
    {
        // A CQRS message with NO [BrokeredMessage] attribute. Registered as a receiver only via the explicit
        // MessageBrokerOptionsBuilder.AddReceiver<TestReceiverMessage>(...) route.
        private class TestReceiverMessage : IMessage
        {
        }

        private static readonly Assembly NoBrokeredMessageAssembly = typeof(IMessage).Assembly;

        private const string InfrastructureType = InMemoryMessagingInfrastructureProvider.InfrastructureType;

        // ------------------------------------------------------------------ probes

        /// <summary>
        /// A scoped <see cref="ICriticalFailureNotifier"/> replacement that is <see cref="IDisposable"/> — the case the
        /// in-box scoped defaults do not cover, because none of them is disposable. Records how many times it was
        /// disposed and snapshots the infrastructure receiver's call log at first disposal so a test can pin that the
        /// scope died AFTER the receive loop unwound.
        /// </summary>
        private sealed class DisposableNotifierProbe : ICriticalFailureNotifier, IDisposable
        {
            private readonly InMemoryMessagingInfrastructureReceiver _infrastructureReceiver;
            private int _disposeCount;

            public DisposableNotifierProbe(InMemoryMessagingInfrastructureReceiver infrastructureReceiver)
                => _infrastructureReceiver = infrastructureReceiver;

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            /// <summary>The infrastructure receiver's call log as it stood when this probe was FIRST disposed.</summary>
            public IReadOnlyList<ReceiverCall> CallLogAtDisposal { get; private set; }

            public Task Notify(FailureContext failureContext) => Task.CompletedTask;

            public void Dispose()
            {
                if (Interlocked.Increment(ref _disposeCount) == 1)
                {
                    CallLogAtDisposal = _infrastructureReceiver.CallLog;
                }
            }
        }

        /// <summary>
        /// A scoped <see cref="ICriticalFailureNotifier"/> replacement that is ONLY <see cref="IAsyncDisposable"/>. A
        /// synchronous scope disposal over this probe throws <see cref="InvalidOperationException"/>, so it pins that
        /// the owning component tears its scope down asynchronously.
        /// </summary>
        private sealed class AsyncOnlyNotifierProbe : ICriticalFailureNotifier, IAsyncDisposable
        {
            private readonly TaskCompletionSource<bool> _releasedSource =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _disposeCount;

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            /// <summary>Completes the first time this probe is released, so a test can WAIT for a release that happens
            /// on a background unwind instead of polling for it.</summary>
            public Task Released => _releasedSource.Task;

            public Task Notify(FailureContext failureContext) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _disposeCount);
                _releasedSource.TrySetResult(true);
                return default;
            }
        }

        /// <summary>
        /// Hands out a scoped <see cref="ICriticalFailureNotifier"/> while counting how many times the registration was
        /// INVOKED, so a test can pin that the receiver graph was never built at all — a stronger claim than that it was
        /// built and later released.
        /// </summary>
        private sealed class CountingNotifierProbeSource
        {
            private readonly ICriticalFailureNotifier _notifier;
            private int _creationCount;

            public CountingNotifierProbeSource(ICriticalFailureNotifier notifier) => _notifier = notifier;

            public int CreationCount => Volatile.Read(ref _creationCount);

            public ICriticalFailureNotifier CreateNotifier(IServiceProvider serviceProvider)
            {
                Interlocked.Increment(ref _creationCount);
                return _notifier;
            }
        }

        /// <summary>
        /// A state store that reports the circuit already OPEN and already in the HALF-OPEN state, so
        /// <see cref="CircuitBreaker.ExecuteAsync{TResult}"/> skips its open-to-half-open wait and lands straight on the
        /// half-open admission primitive — the member a disposed breaker would have already released.
        /// </summary>
        private sealed class AlwaysHalfOpenStateStoreProbe : ICircuitBreakerStateStore
        {
            public Exception LastException => null;
            public DateTime LastStateChangedDateUtc => DateTime.UtcNow;
            public Task OpenAsync(Exception ex) => Task.CompletedTask;
            public Task<int> IncrementFailureCounterAsync(Exception ex) => Task.FromResult(1);
            public Task<int> IncrementSuccessCounterAsync() => Task.FromResult(1);
            public Task CloseAsync() => Task.CompletedTask;
            public Task HalfOpenAsync() => Task.CompletedTask;
            public bool IsClosed => false;
            public CircuitBreakerState State => CircuitBreakerState.HalfOpen;
            public int FailureCount => 0;
            public int SuccessCount => 0;
        }

        // ------------------------------------------------------------------ graph builder

        // Builds the real Chatter graph over the in-memory infrastructure double, with one receiver discovered via the
        // explicit AddReceiver route. preRegistrations runs against the ServiceCollection BEFORE AddChatterCqrs and
        // AddMessageBrokers so probe registrations win over the framework's AddIfNotRegistered defaults.
        // postRegistrations runs AFTER them, where the last descriptor for a service type is the one
        // GetRequiredService resolves, so a probe can REPLACE a service the framework registered unconditionally.
        private static ServiceProvider BuildProvider(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            Action<IServiceCollection> preRegistrations = null,
            Action<MessageBrokerOptionsBuilder> optionsConfigurator = null,
            Action<IServiceCollection> postRegistrations = null)
        {
            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            // A bare ServiceCollection has no logging; the receiver graph depends on ILogger<T>, so register it up front.
            services.AddLogging();

            services.AddSingleton<IMessagingInfrastructure>(BuildInMemoryInfrastructure(infraReceiver));

            preRegistrations?.Invoke(services);

            services
                .AddChatterCqrs(configuration, NoBrokeredMessageAssembly)
                .AddMessageBrokers(
                    optionsBuilder: optionsConfigurator ?? AddTestReceiver,
                    receiverHandlerSourceBuilder: b => b.WithExplicitAssemblies(NoBrokeredMessageAssembly));

            postRegistrations?.Invoke(services);

            return services.BuildServiceProvider();
        }

        private static void AddTestReceiver(MessageBrokerOptionsBuilder builder)
            => builder.AddReceiver<TestReceiverMessage>(
                receiverPath: "test-queue",
                infrastructureType: InfrastructureType);

        // Mirrors InMemoryMessagingInfrastructureProvider's construction so the REAL MessagingInfrastructureProvider
        // resolves the in-memory double by InfrastructureType.
        private static IMessagingInfrastructure BuildInMemoryInfrastructure(
            InMemoryMessagingInfrastructureReceiver infraReceiver)
        {
            var dispatcherFactory = new Mock<IMessagingInfrastructureDispatcherFactory>();
            dispatcherFactory.Setup(f => f.Create()).Returns(new Mock<IMessagingInfrastructureDispatcher>().Object);

            var receiverFactory = new Mock<IMessagingInfrastructureReceiverFactory>();
            receiverFactory.Setup(f => f.Create()).Returns(infraReceiver);

            return new MessagingInfrastructure(
                type: InfrastructureType,
                receiveInfrastructure: receiverFactory.Object,
                dispatchInfrastructure: dispatcherFactory.Object);
        }

        // Resolves the single discovered receiver background service (registered as IHostedService).
        private static IHostedService ResolveReceiverHostedService(ServiceProvider provider)
            => (IHostedService)provider.GetServices<IHostedService>()
                                       .OfType<BrokeredMessageReceiverBackgroundService<TestReceiverMessage>>()
                                       .Single();

        // Awaits a task bounded by a watchdog so a wiring regression fails promptly instead of hanging the run.
        private static async Task AwaitBoundedAsync(Task task, CancellationToken watchdog)
        {
            var watchdogTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (watchdog.Register(() => watchdogTcs.TrySetCanceled(watchdog)))
            {
                var completed = await Task.WhenAny(task, watchdogTcs.Task);
                await completed; // surface OperationCanceledException if the watchdog fired first
            }
        }

        private static InMemoryMessagingInfrastructureReceiver NoMessages()
            => new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 0);

        // ------------------------------------------------------------------ tests

        [Fact]
        public async Task MustKeepScopedGraphAliveUntilTheHostedReceiverStops()
        {
            using var infraReceiver = NoMessages();
            var probe = new DisposableNotifierProbe(infraReceiver);

            using var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services => services.AddScoped<ICriticalFailureNotifier>(_ => probe));

            var hostedService = ResolveReceiverHostedService(provider);
            probe.DisposeCount.Should().Be(0, "the graph is not acquired until the receiver starts");

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await AwaitBoundedAsync(hostedService.StartAsync(watchdog.Token), watchdog.Token);
            probe.DisposeCount.Should().Be(0, "the graph must stay alive while the receiver is receiving");

            await AwaitBoundedAsync(hostedService.StopAsync(watchdog.Token), watchdog.Token);
            probe.DisposeCount.Should().Be(1, "the scope is disposed exactly once, when the receiver's lifetime ends");
        }

        [Fact]
        public async Task MustDisposeAnAsyncOnlyScopedDependencyAsynchronously()
        {
            using var infraReceiver = NoMessages();
            var probe = new AsyncOnlyNotifierProbe();

            using var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services => services.AddScoped<ICriticalFailureNotifier>(_ => probe));

            var hostedService = ResolveReceiverHostedService(provider);
            probe.DisposeCount.Should().Be(0);

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await AwaitBoundedAsync(hostedService.StartAsync(watchdog.Token), watchdog.Token);
            await AwaitBoundedAsync(hostedService.StopAsync(watchdog.Token), watchdog.Token);

            probe.DisposeCount.Should().Be(1, "an async-only scoped dependency must be torn down through DisposeAsync");
        }

        [Fact]
        public void MustNotAcquireTheReceiverGraphUntilTheHostedReceiverStarts()
        {
            using var infraReceiver = NoMessages();
            var probeSource = new CountingNotifierProbeSource(new DisposableNotifierProbe(infraReceiver));

            var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services => services.AddScoped<ICriticalFailureNotifier>(probeSource.CreateNotifier));

            ResolveReceiverHostedService(provider);

            Action disposeHost = () => provider.Dispose();

            disposeHost.Should().NotThrow("a hosted receiver that never started has nothing of its own to release");

            probeSource.CreationCount.Should().Be(
                0,
                "the receiver graph is not acquired until the receive loop opens the scope it lives in");
        }

        [Fact]
        public async Task MustNotAcquireTheReceiverGraphWhenStoppedWithoutStartingAndThenDisposed()
        {
            using var infraReceiver = NoMessages();
            var probeSource = new CountingNotifierProbeSource(new DisposableNotifierProbe(infraReceiver));

            var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services => services.AddScoped<ICriticalFailureNotifier>(probeSource.CreateNotifier));

            var hostedService = ResolveReceiverHostedService(provider);

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            Func<Task> stopReceiver = () => AwaitBoundedAsync(hostedService.StopAsync(watchdog.Token), watchdog.Token);

            await stopReceiver.Should().NotThrowAsync("stopping a receiver that never started has nothing to unwind");

            Action disposeHost = () => provider.Dispose();

            disposeHost.Should().NotThrow("a stopped-then-disposed hosted receiver holds nothing to release");

            probeSource.CreationCount.Should().Be(
                0,
                "a receiver that never started never acquired its graph, so there is nothing to tear down twice");
        }

        [Fact]
        public async Task MustDisposeTheScopeOnlyAfterTheReceiveLoopHasUnwound()
        {
            using var infraReceiver = NoMessages();
            var probe = new DisposableNotifierProbe(infraReceiver);

            using var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services => services.AddScoped<ICriticalFailureNotifier>(_ => probe));

            var hostedService = ResolveReceiverHostedService(provider);

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await AwaitBoundedAsync(hostedService.StartAsync(watchdog.Token), watchdog.Token);
            await AwaitBoundedAsync(hostedService.StopAsync(watchdog.Token), watchdog.Token);

            probe.CallLogAtDisposal.Should().NotBeNull();
            probe.CallLogAtDisposal.Should().Contain(
                ReceiverCall.Dispose,
                "the scope must outlive the receive loop's teardown of the messaging infrastructure");
        }

        [Fact]
        public async Task MustLeaveTheRecoveryCircuitBreakerUsableAfterTheReceiverScopeIsTornDown()
        {
            using var infraReceiver = NoMessages();
            CircuitBreaker resolvedCircuitBreaker = null;

            using var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services =>
                {
                    services.AddScoped<ICircuitBreakerStateStore, AlwaysHalfOpenStateStoreProbe>();
                    services.AddScoped<ICircuitBreaker>(sp =>
                    {
                        resolvedCircuitBreaker = ActivatorUtilities.CreateInstance<CircuitBreaker>(sp);
                        return resolvedCircuitBreaker;
                    });
                });

            // Resolve the receiver from an EXPLICIT scope and tear that scope down, which is what the receive loop
            // itself does at the end of its lifetime. The loop is deliberately NOT started here: an always-half-open
            // store would make a live loop contend for the single half-open slot this test then asserts on.
            //
            // INVARIANT: this guard is green on BOTH sides of the scope-ownership change, because CircuitBreaker
            // declares a public Dispose() but does NOT implement IDisposable (neither does ICircuitBreaker), so the
            // container never captured it as a scope-owned disposable and a torn-down scope never released its
            // half-open admission primitive. The guard is kept so that giving the breaker a disposal contract later
            // cannot reintroduce a half-open path over a released primitive through this seam.
            using (var receiverScope = provider.CreateScope())
            {
                receiverScope.ServiceProvider.GetRequiredService<IBrokeredMessageReceiver<TestReceiverMessage>>();
            }

            resolvedCircuitBreaker.Should().NotBeNull("the circuit breaker is part of the resolved receiver graph");

            var executed = await resolvedCircuitBreaker.ExecuteAsync(_ => Task.FromResult(42));

            executed.Should().Be(42);
        }

        [Fact]
        public async Task MustReleaseThePartiallyBuiltReceiverGraphWhenItCannotBeAcquired()
        {
            using var infraReceiver = NoMessages();
            var probe = new DisposableNotifierProbe(infraReceiver);
            var acquisitionFailure = new InvalidOperationException("the receiver graph cannot be acquired");

            using var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services => services.AddScoped<ICriticalFailureNotifier>(_ => probe),
                postRegistrations: services => services.AddScoped<IBrokeredMessageReceiver<TestReceiverMessage>>(sp =>
                {
                    // Build part of the graph into the scope, THEN fail — exactly the shape of a receiver whose own
                    // constructor throws after its scoped dependencies have been created.
                    sp.GetRequiredService<ICriticalFailureNotifier>();
                    throw acquisitionFailure;
                }));

            var hostedService = ResolveReceiverHostedService(provider);

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            Func<Task> startReceiver = () => AwaitBoundedAsync(hostedService.StartAsync(watchdog.Token), watchdog.Token);

            (await startReceiver.Should().ThrowAsync<InvalidOperationException>(
                    "an unacquirable receiver is startup-fatal and must surface the ORIGINAL failure, not a cleanup failure"))
                .Which.Should().BeSameAs(acquisitionFailure);

            probe.DisposeCount.Should().Be(
                1,
                "the scope holding the partially built graph is released even though the acquisition never completed");
        }

        [Fact]
        public async Task MustReleaseAnAsyncOnlyScopedDependencyWhenTheHostIsDisposedSynchronouslyWithoutStopping()
        {
            using var infraReceiver = NoMessages();
            var probe = new AsyncOnlyNotifierProbe();

            var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services => services.AddScoped<ICriticalFailureNotifier>(_ => probe));

            var hostedService = ResolveReceiverHostedService(provider);

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await AwaitBoundedAsync(hostedService.StartAsync(watchdog.Token), watchdog.Token);

            // A synchronous host disposal with NO stop is the path a startup-fatal failure takes: StopAsync never runs
            // and the container falls back to the SYNCHRONOUS disposal, which an async-only scoped member refuses.
            Action disposeHostSynchronously = () => provider.Dispose();

            disposeHostSynchronously.Should().NotThrow(
                "a synchronous host disposal must not raise a disposal failure that would mask the original one");

            // The receive loop unwinds in the background, so wait on the probe's own signal rather than polling.
            await AwaitBoundedAsync(probe.Released, watchdog.Token);

            probe.DisposeCount.Should().Be(1, "the async-only scoped dependency is released exactly once");
        }
    }
}
