#nullable disable

using Chatter.CQRS;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.DependencyInjection.UsingChatterMessageBrokerExtensions
{
    /// <summary>
    /// End-to-end startup tests that boot the REAL Chatter DI graph (AddChatterCqrs -> AddMessageBrokers) over the
    /// existing in-memory infrastructure double and exercise both <c>ChatterMessageBrokerExtensions</c> registration
    /// wiring and the <c>BrokeredMessageReceiverBackgroundService</c> hosted-service lifecycle without any external
    /// broker.
    ///
    /// INVARIANT: assembly scanning is scoped explicitly to a marker assembly that contains NO
    /// <c>[BrokeredMessage]</c>-decorated <see cref="IMessage"/> types, so attribute-driven receiver discovery is
    /// deterministically empty and only the explicit <c>AddReceiver&lt;TMessage&gt;</c> route contributes receivers.
    /// <see cref="TestReceiverMessage"/> deliberately carries NO <c>[BrokeredMessage]</c> attribute (the attribute
    /// scan is assembly-global; introducing a decorated type here would pollute discovery across other test
    /// assemblies in the run).
    /// </summary>
    public class WhenBootingHost : Testing.Core.Context
    {
        // A CQRS message with NO [BrokeredMessage] attribute. Registered as a receiver only via the explicit
        // MessageBrokerOptionsBuilder.AddReceiver<TestReceiverMessage>(...) route.
        private class TestReceiverMessage : IMessage
        {
        }

        // The CQRS marker assembly is the Chatter.CQRS assembly, which contains no [BrokeredMessage]-decorated
        // IMessage types, so the attribute-driven receiver scan is deterministically empty. The explicit
        // AddReceiver route is the only contributor of receivers.
        private static readonly Assembly NoBrokeredMessageAssembly = typeof(IMessage).Assembly;

        private const string InfrastructureType = InMemoryMessagingInfrastructureProvider.InfrastructureType;

        // Builds the real Chatter graph over the in-memory infrastructure double. The supplied receiver double is
        // wrapped in a MessagingInfrastructure(type, receiverFactory, dispatcherFactory) registered as the single
        // IMessagingInfrastructure, so the REAL MessagingInfrastructureProvider resolves it by InfrastructureType.
        // optionsConfigurator runs against the live MessageBrokerOptionsBuilder (e.g. to AddReceiver or set a
        // transaction mode). Assembly scanning is scoped to NoBrokeredMessageAssembly on both CQRS and broker sides.
        private static ServiceProvider BuildProvider(
            InMemoryMessagingInfrastructureReceiver infraReceiver,
            Action<MessageBrokerOptionsBuilder> optionsConfigurator = null)
        {
            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            // A bare ServiceCollection has no logging; the receiver graph (MessagingInfrastructureProvider and the
            // hosted services it backs) depends on ILogger<T>, so register it up front.
            services.AddLogging();

            services.AddSingleton<IMessagingInfrastructure>(BuildInMemoryInfrastructure(infraReceiver));

            services
                .AddChatterCqrs(configuration, NoBrokeredMessageAssembly)
                .AddMessageBrokers(
                    optionsBuilder: optionsConfigurator,
                    receiverHandlerSourceBuilder: b => b.WithExplicitAssemblies(NoBrokeredMessageAssembly));

            return services.BuildServiceProvider();
        }

        // Mirrors InMemoryMessagingInfrastructureProvider's construction: wraps the in-memory receiver in a
        // MessagingInfrastructure whose Type matches the InfrastructureType supplied to AddReceiver, so the real
        // MessagingInfrastructureProvider resolves it.
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

        // Resolves the single discovered receiver background service for TestReceiverMessage from the registered
        // IHostedService singletons (it is registered as IHostedService, not BackgroundService).
        private static BrokeredMessageReceiverBackgroundService<TestReceiverMessage> ResolveReceiverHostedService(
            ServiceProvider provider)
            => provider.GetServices<IHostedService>()
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

        // ------------------------------------------------------------------ (1) service registrations

        [Fact]
        public void MustRegisterCoreMessageBrokerServices()
        {
            using var infraReceiver = NoMessages();
            using var provider = BuildProvider(infraReceiver);

            // Singletons resolvable from the root provider.
            provider.GetRequiredService<IMessagingInfrastructureProvider>()
                    .Should().BeOfType<MessagingInfrastructureProvider>();
            provider.GetRequiredService<MessageBrokerOptions>().Should().NotBeNull();

            // Scoped services must be resolved inside a scope, never from the root provider.
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            // IExternalDispatcher is replaced by the broker's BrokeredMessageDispatcher.
            sp.GetRequiredService<IExternalDispatcher>().Should().BeOfType<BrokeredMessageDispatcher>();
            sp.GetRequiredService<IBrokeredMessageDispatcher>().Should().BeOfType<BrokeredMessageDispatcher>();

            sp.GetRequiredService<IBrokeredMessageReceiverFactory>().Should().NotBeNull();

            // RouteMessagesToOutbox defaults false, so the non-outbox router branch is registered.
            sp.GetRequiredService<IRouteBrokeredMessages>().Should().BeOfType<BrokeredMessageRouter>();

            sp.GetRequiredService<IBrokeredMessageOutbox>()
              .Should().BeOfType<Chatter.MessageBrokers.Reliability.InMemoryBrokeredMessageOutbox>();
            sp.GetRequiredService<IBrokeredMessageInbox>().Should().BeOfType<InMemoryBrokeredMessageInbox>();

            sp.GetRequiredService<IBodyConverterFactory>().Should().NotBeNull();
            sp.GetServices<IBrokeredMessageBodyConverter>().Should().HaveCountGreaterThanOrEqualTo(2);
        }

        // ------------------------------------------------------------------ (2) options binding

        [Fact]
        public void MustBindMessageBrokerOptionsWithDefaults()
        {
            using var infraReceiver = NoMessages();
            using var provider = BuildProvider(infraReceiver);

            var options = provider.GetRequiredService<MessageBrokerOptions>();

            options.Should().NotBeNull();
            options.Reliability.Should().NotBeNull();
            options.Recovery.Should().NotBeNull();
            options.TransactionMode.Should().Be(TransactionMode.ReceiveOnly);
        }

        [Fact]
        public void MustFlowConfiguredTransactionModeThroughOptions()
        {
            using var infraReceiver = NoMessages();
            using var provider = BuildProvider(
                infraReceiver,
                optionsConfigurator: b => b.WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure));

            provider.GetRequiredService<MessageBrokerOptions>()
                    .TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
        }

        // ------------------------------------------------------------------ (3) receiver discovery (deterministic)

        [Fact]
        public void MustDiscoverExactlyOneReceiverWhenOneIsAdded()
        {
            using var infraReceiver = NoMessages();
            using var provider = BuildProvider(
                infraReceiver,
                optionsConfigurator: b => b.AddReceiver<TestReceiverMessage>(
                    receiverPath: "test-queue",
                    infrastructureType: InfrastructureType));

            provider.GetServices<IHostedService>()
                    .OfType<BrokeredMessageReceiverBackgroundService<TestReceiverMessage>>()
                    .Should().ContainSingle();

            var registry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();
            registry.DiscoveredReceivers.Should().ContainSingle();
            var retained = registry.DiscoveredReceivers.Single();
            retained.MessageReceiverPath.Should().Be("test-queue");
            retained.InfrastructureType.Should().Be(InfrastructureType);
        }

        [Fact]
        public void MustDiscoverNoReceiversWhenNoneAreAdded()
        {
            using var infraReceiver = NoMessages();
            using var provider = BuildProvider(infraReceiver);

            provider.GetServices<IHostedService>()
                    .OfType<BrokeredMessageReceiverBackgroundService<TestReceiverMessage>>()
                    .Should().BeEmpty();

            // The registry is registered only when at least one receiver is discovered; with none added it may be
            // absent. When present, it must hold no receivers.
            var registry = provider.GetService<IDiscoveredReceiverRegistry>();
            (registry?.DiscoveredReceivers ?? Array.Empty<ReceiverOptions>()).Should().BeEmpty();
        }

        // ------------------------------------------------------------------ (4) hosted start/stop lifecycle

        [Fact]
        public async Task MustStartAndStopDiscoveredReceiverHostedServiceCleanly()
        {
            // Enqueue one message so the discovered receiver provably pumps: Drained completes when the receive loop
            // dequeues it (before dispatch), proving the hosted service drove the in-memory receiver end-to-end.
            using var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            infraReceiver.Enqueue(BuildContext());

            using var provider = BuildProvider(
                infraReceiver,
                optionsConfigurator: b => b.AddReceiver<TestReceiverMessage>(
                    receiverPath: "test-queue",
                    infrastructureType: InfrastructureType));

            var hostedService = (IHostedService)ResolveReceiverHostedService(provider);

            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // StartAsync returns deterministically once the receiver goes live (ReceivingStarted completes).
            await AwaitBoundedAsync(hostedService.StartAsync(watchdog.Token), watchdog.Token);

            // The pump drove the in-memory receiver: it initialized and dequeued the enqueued message.
            await AwaitBoundedAsync(infraReceiver.Drained, watchdog.Token);
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Initialize);
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Receive);

            // StopAsync unwinds the loop (empty-queue receive parks on Task.Delay(Infinite, token) and cancels
            // cleanly) and tears the receiver down. The infrastructure receiver is async-disposed on unwind, which
            // is the observable clean-teardown signal through this hosted-service path.
            await AwaitBoundedAsync(hostedService.StopAsync(watchdog.Token), watchdog.Token);
            infraReceiver.CallLog.Should().Contain(ReceiverCall.Dispose);
        }

        // ------------------------------------------------------------------ helpers

        private static InMemoryMessagingInfrastructureReceiver NoMessages()
            => new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 0);

        // Builds a MessageBrokerContext whose body deserialises as TestReceiverMessage; dispatch of an unhandled
        // message faults AFTER Drained fires (Drained completes at dequeue), so awaiting Drained stays deterministic.
        private static Chatter.MessageBrokers.Context.MessageBrokerContext BuildContext()
        {
            var converter = new JsonBodyConverter();
            return new Chatter.MessageBrokers.Context.MessageBrokerContext(
                messageId: Guid.NewGuid().ToString(),
                body: converter.Convert(new TestReceiverMessage()),
                applicationProperties: new System.Collections.Generic.Dictionary<string, object>(),
                messageReceiverPath: "test-queue",
                receiverCancellationToken: CancellationToken.None,
                bodyConverter: converter);
        }
    }
}
