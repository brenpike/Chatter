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
    /// belongs to the hosted background service whose lifetime bounds that graph, so every scoped member of the graph
    /// stays alive for as long as the receiver does and is disposed only when the receiver's own lifetime ends.
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
            private int _disposeCount;

            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public Task Notify(FailureContext failureContext) => Task.CompletedTask;

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref _disposeCount);
                return default;
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
        private static ServiceProvider BuildProvider(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            Action<IServiceCollection> preRegistrations = null,
            Action<MessageBrokerOptionsBuilder> optionsConfigurator = null)
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
            probe.DisposeCount.Should().Be(0, "resolving the hosted service must not dispose the graph it keeps");

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
        public void MustDisposeTheScopeWhenTheHostedReceiverIsDisposedWithoutEverStarting()
        {
            using var infraReceiver = NoMessages();
            var probe = new DisposableNotifierProbe(infraReceiver);

            var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services => services.AddScoped<ICriticalFailureNotifier>(_ => probe));

            ResolveReceiverHostedService(provider);

            provider.Dispose();

            probe.DisposeCount.Should().Be(1, "a hosted receiver disposed without ever starting still owns its scope");
        }

        [Fact]
        public async Task MustDisposeTheScopeOnceWhenStoppedWithoutStartingAndThenDisposed()
        {
            using var infraReceiver = NoMessages();
            var probe = new DisposableNotifierProbe(infraReceiver);

            var provider = BuildProvider(
                infraReceiver,
                preRegistrations: services => services.AddScoped<ICriticalFailureNotifier>(_ => probe));

            var hostedService = ResolveReceiverHostedService(provider);

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await AwaitBoundedAsync(hostedService.StopAsync(watchdog.Token), watchdog.Token);
            provider.Dispose();

            probe.DisposeCount.Should().Be(1, "scope disposal is idempotent, so stop-then-dispose tears it down once");
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
        public async Task MustLeaveTheRecoveryCircuitBreakerUsableAfterTheHostedReceiverIsResolved()
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

            // Resolution alone is the moment a registration-factory-owned scope would be torn down. The receive loop is
            // deliberately NOT started: an always-half-open store would make it contend for the single half-open slot.
            //
            // INVARIANT: this guard is green on BOTH sides of the scope-ownership change, because CircuitBreaker
            // declares a public Dispose() but does NOT implement IDisposable (neither does ICircuitBreaker), so the
            // container never captured it as a scope-owned disposable and a torn-down scope never released its
            // half-open admission primitive. The guard is kept so that giving the breaker a disposal contract later
            // cannot reintroduce a half-open path over a released primitive through this seam.
            ResolveReceiverHostedService(provider);

            resolvedCircuitBreaker.Should().NotBeNull("the circuit breaker is part of the resolved receiver graph");

            var executed = await resolvedCircuitBreaker.ExecuteAsync(_ => Task.FromResult(42));

            executed.Should().Be(42);
        }
    }
}
