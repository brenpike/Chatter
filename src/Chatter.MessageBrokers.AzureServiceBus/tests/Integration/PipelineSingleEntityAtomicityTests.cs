using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Single-entity FullAtomicityViaInfrastructure coverage driven THROUGH Chatter. Each test instance leases
    // its OWN queue from the emulator fixture's pool (xUnit constructs a new test-class instance per test), so
    // a message one test strands on its entity can never be consumed by another. A receiver on the leased queue
    // runs in TransactionMode.FullAtomicityViaInfrastructure; when its handler is invoked for the original
    // message it sends a follow-up to the SAME entity (the leased queue) via the broker context's Send (which
    // enlists in the receiver's atomic TransactionScope). The scope commits, settling the original and
    // committing the follow-up send atomically. Because both operations target a single top-level entity,
    // this stays within what the Azure Service Bus emulator supports (it rejects only multi-top-level-entity /
    // cross-entity transactions), so the emulator CI lane can run it.
    //
    // Everything observable goes through Chatter: the send is IBrokeredMessageDispatcher.Send, the follow-up
    // is IMessageBrokerContext.Send from inside the handler, and the proof is a SECOND Chatter handling (the
    // follow-up delivered back to the same handler). No Azure SDK type is used.
    //
    // All facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineSingleEntityAtomicityTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly ServiceBusEmulatorFixture _emulator;

        // INVARIANT: leased per test-class INSTANCE, never static. xUnit constructs a new instance per test
        // method, so every test — present and future — gets its own entity and cannot observe another test's
        // stranded messages.
        private readonly string _atomicQueue;

        public PipelineSingleEntityAtomicityTests(ServiceBusEmulatorFixture emulator)
        {
            _emulator = emulator;
            _atomicQueue = emulator.LeaseQueue();
        }

        // IsFollowUp distinguishes the original message (which triggers the follow-up send) from the follow-up
        // itself (which must NOT re-trigger, avoiding unbounded recursion on the same entity).
        public sealed class AtomicCommand : ICommand
        {
            public string Value { get; set; }
            public bool IsFollowUp { get; set; }
        }

        // Carries the per-test leased queue name to the DI-resolved handlers, which can no longer close over a
        // class constant. Registered as a SINGLETON because handler resolution is transient per receive scope
        // and both the original and the follow-up delivery must target the same leased entity.
        private sealed class AtomicQueueTarget
        {
            public AtomicQueueTarget(string queueName)
                => QueueName = queueName ?? throw new ArgumentNullException(nameof(queueName));

            public string QueueName { get; }
        }

        // A Chatter IMessageHandler<AtomicCommand> that, on the ORIGINAL message, sends a follow-up to the
        // SAME entity through the broker context (enlisting in the receiver's FullAtomicityViaInfrastructure
        // scope), then records every invocation through the shared registry so the test can observe both the
        // original consumption and the follow-up delivery via Chatter. Registered explicitly into the test DI
        // graph (not via the harness's RecordingMessageHandler wiring) so it can perform the forward.
        private sealed class ForwardingAtomicHandler : IMessageHandler<AtomicCommand>
        {
            private readonly HandlerSignalRegistry _registry;
            private readonly AtomicQueueTarget _queueTarget;

            public ForwardingAtomicHandler(HandlerSignalRegistry registry, AtomicQueueTarget queueTarget)
            {
                _registry = registry ?? throw new ArgumentNullException(nameof(registry));
                _queueTarget = queueTarget ?? throw new ArgumentNullException(nameof(queueTarget));
            }

            public async Task Handle(AtomicCommand message, IMessageHandlerContext context)
            {
                _registry.GetOrAdd<AtomicCommand>().Record(
                    new HandledRecord<AtomicCommand>(message, context as IMessageBrokerContext));

                if (!message.IsFollowUp)
                {
                    // Send the follow-up to the SAME entity via the broker context so it enlists in the
                    // receiver's atomic scope; the scope commits the settle of the original and this send
                    // together.
                    await context.Send(
                        new AtomicCommand { Value = message.Value + "-followup", IsFollowUp = true },
                        _queueTarget.QueueName);
                }
            }
        }

        // Records, across all invocations, whether the handler was ever invoked for a FOLLOW-UP message — the
        // HandlerSignal only retains the LAST record, so a dedicated flag is needed to prove the follow-up was
        // NEVER delivered over the whole (redelivered) lifetime, not merely on the last poll. Registered as a
        // singleton so the test and the DI-resolved handler share one instance.
        private sealed class FollowUpObserver
        {
            private int _followUpHandledCount;

            public int FollowUpHandledCount => Volatile.Read(ref _followUpHandledCount);

            public void RecordFollowUp() => Interlocked.Increment(ref _followUpHandledCount);
        }

        // The rollback counterpart of ForwardingAtomicHandler: on the ORIGINAL message it sends a follow-up to
        // the SAME entity within the receiver's atomic scope and THEN THROWS before returning, so the
        // TransactionScope never completes and the enlisted follow-up send is rolled back (never delivered). It
        // records every invocation (via the signal registry, so the test can observe redelivery) and flags any
        // follow-up sighting on the shared FollowUpObserver so the test can prove no follow-up is ever handled.
        private sealed class RollbackAtomicHandler : IMessageHandler<AtomicCommand>
        {
            private readonly HandlerSignalRegistry _registry;
            private readonly FollowUpObserver _followUpObserver;
            private readonly AtomicQueueTarget _queueTarget;

            public RollbackAtomicHandler(
                HandlerSignalRegistry registry,
                FollowUpObserver followUpObserver,
                AtomicQueueTarget queueTarget)
            {
                _registry = registry ?? throw new ArgumentNullException(nameof(registry));
                _followUpObserver = followUpObserver ?? throw new ArgumentNullException(nameof(followUpObserver));
                _queueTarget = queueTarget ?? throw new ArgumentNullException(nameof(queueTarget));
            }

            public async Task Handle(AtomicCommand message, IMessageHandlerContext context)
            {
                _registry.GetOrAdd<AtomicCommand>().Record(
                    new HandledRecord<AtomicCommand>(message, context as IMessageBrokerContext));

                if (message.IsFollowUp)
                {
                    // A follow-up reaching the handler would mean the rolled-back send was delivered — the
                    // failure this test guards against. Flag it and stop (no further send) so the test can assert
                    // it never happens.
                    _followUpObserver.RecordFollowUp();
                    return;
                }

                // Send the follow-up to the SAME entity so it enlists in the receiver's atomic scope, then throw
                // BEFORE returning so the scope never completes — the follow-up send must roll back with it.
                await context.Send(
                    new AtomicCommand { Value = message.Value + "-followup", IsFollowUp = true },
                    _queueTarget.QueueName);

                throw new InvalidOperationException(
                    "force the atomic scope to roll back after enlisting the follow-up send");
            }
        }

        // FullAtomicityViaInfrastructure on a single entity: the original is consumed and the follow-up it
        // sends (within the atomic scope) is delivered back to the same handler. Observing the follow-up
        // handling proves the send committed atomically with the original's settle.
        [RequiresDockerFact]
        public async Task FullAtomicityCommitsFollowUpSentToSameEntityFromHandler()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    sb.AddQueueReceiver<AtomicCommand>(_atomicQueue, transactionMode: TransactionMode.FullAtomicityViaInfrastructure);
                    // The leased queue name the handlers send their follow-up to; singleton so both the original
                    // and the follow-up receive scope resolve the same target.
                    sb.Services.AddSingleton(new AtomicQueueTarget(_atomicQueue));
                    // Register the forwarding handler as the IMessageHandler<AtomicCommand> Chatter resolves on
                    // the receive path (instead of the harness's RecordingMessageHandler), so the handler can
                    // emit the follow-up within the atomic scope.
                    sb.Services.AddTransient<IMessageHandler<AtomicCommand>, ForwardingAtomicHandler>();
                });
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new AtomicCommand { Value = "atomic" }, _atomicQueue);
            }

            // First handling: the original message.
            var firstHandling = await harness.WaitForHandledAsync<AtomicCommand>(HandlerWait);
            firstHandling.Message.Value.Should().Be("atomic");
            firstHandling.Message.IsFollowUp.Should().BeFalse("the original message is consumed first");

            // The follow-up — sent within the committed atomic scope — must be delivered back to the handler,
            // proving the send was committed atomically with the original's settle. Two invocations total:
            // the original plus the follow-up.
            var observed = await harness.WaitForInvocationCountAsync<AtomicCommand>(2, HandlerWait);
            observed.Should().BeGreaterThanOrEqualTo(2,
                "the follow-up sent within the committed atomic scope must be delivered to the same entity");

            // Assert the actual follow-up PAYLOAD was delivered, not merely that the count reached 2. In the
            // failure case the count could climb to 2 via a redelivery of the ORIGINAL (which is also recorded
            // before the send) without the committed follow-up ever arriving; requiring a handled record whose
            // IsFollowUp == true proves the follow-up send was committed and delivered, not inferred from the
            // count.
            harness.GetSignal<AtomicCommand>().Records
                .Should().Contain(
                    r => r.Message.IsFollowUp && r.Message.Value == "atomic-followup",
                    "the committed atomic scope must deliver the follow-up payload to the same entity, not just " +
                    "redeliver the original");
        }

        // FullAtomicityViaInfrastructure rollback (the commit test's counterpart): the handler sends a follow-up
        // to the SAME entity within the atomic scope and then THROWS, so the TransactionScope never completes.
        // The enlisted follow-up send must roll back (the follow-up is NEVER delivered) AND the original is
        // abandoned on the PeekLock and REDELIVERED to the handler. This is the rollback half of the atomicity
        // guarantee — the original's settle and the follow-up send fail together. maxReceiveAttempts bounds the
        // redelivery so the original eventually deadletters instead of looping forever.
        [RequiresDockerFact]
        public async Task FullAtomicityRollsBackFollowUpWhenHandlerThrowsAndRedeliversOriginal()
        {
            var followUpObserver = new FollowUpObserver();

            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    sb.AddQueueReceiver<AtomicCommand>(
                        _atomicQueue,
                        transactionMode: TransactionMode.FullAtomicityViaInfrastructure,
                        maxReceiveAttempts: 3);
                    // The leased queue name the handlers send their follow-up to; singleton so both the original
                    // and the follow-up receive scope resolve the same target.
                    sb.Services.AddSingleton(new AtomicQueueTarget(_atomicQueue));
                    // The shared observer the test reads to prove no follow-up ever reaches the handler.
                    sb.Services.AddSingleton(followUpObserver);
                    // The rollback handler sends the follow-up then throws, so the atomic scope never completes.
                    sb.Services.AddTransient<IMessageHandler<AtomicCommand>, RollbackAtomicHandler>();
                });
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new AtomicCommand { Value = "rollback" }, _atomicQueue);
            }

            // First handling: the original message (which sends the follow-up, then throws).
            var firstHandling = await harness.WaitForHandledAsync<AtomicCommand>(HandlerWait);
            firstHandling.Message.Value.Should().Be("rollback");
            firstHandling.Message.IsFollowUp.Should().BeFalse("the original message is consumed first");

            // The original is abandoned on the thrown handler and REDELIVERED (PeekLock), so the handler is
            // invoked again — proving the original's settle rolled back with the scope.
            var observed = await harness.WaitForInvocationCountAsync<AtomicCommand>(2, HandlerWait);
            observed.Should().BeGreaterThanOrEqualTo(2,
                "the original is abandoned when the handler throws and must be redelivered (the settle rolled back)");

            // The follow-up send enlisted in the never-completed scope must have rolled back: across the entire
            // redelivered lifetime the handler is NEVER invoked for a follow-up. The window is generous enough
            // that a delivered follow-up (if the rollback were broken) would have landed within it.
            followUpObserver.FollowUpHandledCount.Should().Be(
                0,
                "the follow-up sent inside the never-completed atomic scope must roll back and never be delivered");
        }
    }
}
