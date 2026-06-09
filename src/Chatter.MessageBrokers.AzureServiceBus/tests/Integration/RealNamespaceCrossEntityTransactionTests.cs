using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Real-namespace proof of the cross-entity transaction guarantee that Chatter's
    // FullAtomicityViaInfrastructure mode relies on, driven THROUGH Chatter's pipeline (NOT the raw SDK). The
    // Azure Service Bus emulator CANNOT exercise these — cross-entity (multi-top-level-entity) transactions
    // throw "Local transactions cannot span multiple top-level entities" on the emulator — so these tests
    // target a REAL Azure Service Bus namespace.
    //
    // SYSTEM UNDER TEST = Chatter's pipeline. A receiver on QueueA runs in
    // TransactionMode.FullAtomicityViaInfrastructure; its handler forwards to QueueB via the broker context's
    // Send (IMessageBrokerContext.Send), which enlists in the receiver's atomic TransactionScope on the shared
    // EnableCrossEntityTransactions client. The committed/rolled-back observable behavior is asserted purely
    // through Chatter: a RecordingMessageHandler on QueueB observes the forwarded message (or its absence), and
    // the QueueA handler's invocation count observes redelivery.
    //
    // CRITICAL — TRAIT: this class carries ONLY [Trait("Category","RealNamespaceIntegration")] and NOT the
    // Integration trait. xUnit traits are additive, so an Integration trait here would let the emulator CI
    // lane (`--filter Category=Integration`) re-select these and fail on the emulator. They run only in the
    // dedicated real-namespace CI lane and locally when CHATTER_ASB_REAL_NAMESPACE_CONNECTION_STRING is set.
    //
    // All facts are gated by [RequiresRealServiceBusNamespaceFact] and are SKIPPED (never failed) when no
    // real-namespace connection string is configured, so a plain `dotnet test` stays green.
    [Trait("Category", "RealNamespaceIntegration")]
    [Collection(RealNamespaceCrossEntityTransactionCollection.Name)]
    public class RealNamespaceCrossEntityTransactionTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);
        // A window long enough that a forwarded message (if it were going to arrive) or a redelivery would land
        // within it, used to assert the ABSENCE of a forward and to observe source redelivery on rollback.
        private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(20);

        private readonly RealNamespaceCrossEntityTransactionFixture _namespace;

        public RealNamespaceCrossEntityTransactionTests(RealNamespaceCrossEntityTransactionFixture @namespace)
            => _namespace = @namespace;

        // The command consumed on QueueA. Its handler forwards a ForwardedCommand to QueueB within the
        // receiver's FullAtomicityViaInfrastructure scope.
        public sealed class SourceCommand : ICommand
        {
            public string Value { get; set; }
        }

        // The command forwarded to QueueB; a RecordingMessageHandler on QueueB observes it (or its absence).
        public sealed class ForwardedCommand : ICommand
        {
            public string Value { get; set; }
        }

        // A handler on QueueA that forwards to QueueB via the broker context's Send (enlisting in the atomic
        // scope), then OPTIONALLY throws after the forward so the atomic scope does not complete — exercising
        // the rolled-back cross-entity path. Records every invocation of SourceCommand through the shared
        // registry so the test can observe redelivery on rollback. The forward target queue and the
        // throw-after-forward behavior are injected so one handler serves both the committed and rolled-back
        // cases.
        private sealed class ForwardingSourceHandler : IMessageHandler<SourceCommand>
        {
            private readonly HandlerSignalRegistry _registry;
            private readonly string _destinationQueue;
            private readonly bool _throwAfterForward;

            public ForwardingSourceHandler(HandlerSignalRegistry registry, string destinationQueue, bool throwAfterForward)
            {
                _registry = registry ?? throw new ArgumentNullException(nameof(registry));
                _destinationQueue = destinationQueue ?? throw new ArgumentNullException(nameof(destinationQueue));
                _throwAfterForward = throwAfterForward;
            }

            public async Task Handle(SourceCommand message, IMessageHandlerContext context)
            {
                var brokerContext = context as IMessageBrokerContext;
                _registry.GetOrAdd<SourceCommand>().Record(
                    new HandledRecord<SourceCommand>(message, brokerContext));

                // Forward within the receiver's atomic scope. On the committed path the scope completes after
                // the handler returns, committing this send together with the source settle. On the rolled-back
                // path the throw below prevents the scope completing, rolling BOTH back.
                await brokerContext.Send(
                    new ForwardedCommand { Value = message.Value + "-forwarded" },
                    _destinationQueue);

                if (_throwAfterForward)
                {
                    throw new InvalidOperationException(
                        "force rollback of the cross-entity atomic scope after the forward");
                }
            }
        }

        private ChatterPipelineHarness BuildHarness(bool throwAfterForward)
            => ChatterPipelineHarness.Build(
                _namespace.GetConnectionString(),
                sb =>
                {
                    // Source receiver on the fixture's unique QueueA in FullAtomicityViaInfrastructure; its
                    // forwarding handler enlists the forward in the atomic scope. Registered explicitly so
                    // Chatter resolves the forwarding handler for SourceCommand.
                    sb.AddQueueReceiver<SourceCommand>(
                        _namespace.QueueA,
                        transactionMode: TransactionMode.FullAtomicityViaInfrastructure);
                    sb.Services.AddTransient<IMessageHandler<SourceCommand>>(sp =>
                        new ForwardingSourceHandler(
                            sp.GetRequiredService<HandlerSignalRegistry>(),
                            _namespace.QueueB,
                            throwAfterForward));

                    // Dest receiver on the fixture's unique QueueB; its RecordingMessageHandler<ForwardedCommand>
                    // (wired by the harness via the messageTypes arg) observes the forwarded message.
                    sb.AddQueueReceiver<ForwardedCommand>(_namespace.QueueB);
                },
                typeof(ForwardedCommand));

        // Committed cross-entity transaction THROUGH Chatter: the QueueA handler forwards to QueueB and returns
        // normally, so Chatter completes the atomic scope — committing the forward and the source settle
        // together. QueueB receives the forwarded message (observed via Chatter's receive path) and QueueA's
        // source is consumed (handled exactly once, no redelivery).
        [RequiresRealServiceBusNamespaceFact]
        public async Task CommittedCrossEntityTransactionConsumesSourceAndDeliversToDestination()
        {
            await using var harness = BuildHarness(throwAfterForward: false);
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new SourceCommand { Value = "commit" }, _namespace.QueueA);
            }

            // The forwarded message is delivered to QueueB through Chatter's receive path — the committed
            // cross-entity guarantee FullAtomicityViaInfrastructure depends on.
            var delivered = await harness.WaitForHandledAsync<ForwardedCommand>(HandlerWait);
            delivered.Message.Value.Should().Be(
                "commit-forwarded",
                "the forwarded message must arrive on QueueB when the atomic scope commits");

            // The source on QueueA was consumed in the committed scope: the handler is invoked exactly once and
            // never redelivered.
            var sourceInvocations = await harness.WaitForInvocationCountAsync<SourceCommand>(2, ObservationWindow);
            sourceInvocations.Should().Be(
                1,
                "the source message is settled in the committed atomic scope, so it is never redelivered");
        }

        // Rolled-back cross-entity transaction THROUGH Chatter: the QueueA handler forwards to QueueB and then
        // throws BEFORE the atomic scope completes, so Chatter rolls BOTH the forward and the source settle
        // back. QueueB receives NOTHING and the source message is redelivered (the QueueA handler is invoked
        // again because the settle rolled back).
        [RequiresRealServiceBusNamespaceFact]
        public async Task RolledBackCrossEntityTransactionDeliversNothingAndRedeliversSource()
        {
            await using var harness = BuildHarness(throwAfterForward: true);
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new SourceCommand { Value = "rollback" }, _namespace.QueueA);
            }

            // The source is redelivered because the settle rolled back with the scope: the handler is invoked
            // more than once.
            var sourceInvocations = await harness.WaitForInvocationCountAsync<SourceCommand>(2, HandlerWait);
            sourceInvocations.Should().BeGreaterThanOrEqualTo(
                2,
                "the source must be redelivered when the atomic scope rolls back without completing");

            // The forward rolled back with the scope, so QueueB receives nothing within the observation window.
            var forwardArrived = await harness.WaitForInvocationCountAsync<ForwardedCommand>(1, ObservationWindow);
            forwardArrived.Should().Be(
                0,
                "the forwarded send must roll back with the atomic scope, so QueueB receives nothing");

            // On rollback the source is redelivered, and the QueueA receiver in BuildHarness uses the default
            // maxReceiveAttempts (10), so the 'rollback' source is still active/locked on the fixture's shared
            // QueueA at this point — NOT yet dead-lettered. Disposing the harness now could leave that stale
            // source active; if the committed test then runs on the same QueueA it could consume the leftover
            // and forward 'rollback-forwarded', making its commit assertion order-dependent. Drain QueueA
            // (bounded) so the rolled-back source is settled before teardown.
            await DrainQueueAsync(_namespace.QueueA, HandlerWait);
        }

        // A single empty ReceiveAndDelete read is NOT proof the queue is drained: the rolled-back source can
        // still be held under PeekLock by Chatter's receiver (the QueueA receiver uses the default
        // maxReceiveAttempts of 10, so the source is redelivered/locked, not yet dead-lettered), so a separate
        // receiver sees no AVAILABLE message and reads null even though the source is not yet settled. Requiring
        // this many CONSECUTIVE empty reads, each over a real wait window, makes the drain wait past that
        // transient unavailability before declaring the queue quiet.
        private const int DrainQuietReads = 3;

        // Bounded drain of a real-namespace queue: ReceiveAndDelete-drains until it stays empty across
        // DrainQuietReads CONSECUTIVE empty reads (a single null read is treated as transient unavailability,
        // not as drained), bounded by an overall timeout, so a rolled-back/redelivered source left active at
        // teardown cannot leak into a later test on the same shared queue. Bounded so it never hangs CI.
        private async Task DrainQueueAsync(string queue, TimeSpan timeout)
        {
            await using var client = new ServiceBusClient(_namespace.GetConnectionString());
            var receiver = client.CreateReceiver(queue, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
            });

            var deadline = DateTime.UtcNow + timeout;
            var consecutiveEmpty = 0;
            while (DateTime.UtcNow < deadline)
            {
                var leftover = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
                if (leftover is null)
                {
                    // A null read may be transient (the source is locked, not gone). Only treat the queue as
                    // drained once it stays empty across several consecutive reads.
                    if (++consecutiveEmpty >= DrainQuietReads)
                    {
                        return;
                    }

                    continue;
                }

                // A delivered message resets the quiet streak: something was still active, so keep draining.
                consecutiveEmpty = 0;
            }
        }
    }
}
