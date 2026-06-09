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
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Single-entity FullAtomicityViaInfrastructure coverage driven THROUGH Chatter. A receiver on
    // chatter.atomic runs in TransactionMode.FullAtomicityViaInfrastructure; when its handler is invoked for
    // the original message it sends a follow-up to the SAME entity (chatter.atomic) via the broker context's
    // Send (which enlists in the receiver's atomic TransactionScope). The scope commits, settling the original
    // and committing the follow-up send atomically. Because both operations target a single top-level entity,
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
        private const string AtomicQueue = "chatter.atomic";
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineSingleEntityAtomicityTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        // IsFollowUp distinguishes the original message (which triggers the follow-up send) from the follow-up
        // itself (which must NOT re-trigger, avoiding unbounded recursion on the same entity).
        public sealed class AtomicCommand : ICommand
        {
            public string Value { get; set; }
            public bool IsFollowUp { get; set; }
        }

        // A Chatter IMessageHandler<AtomicCommand> that, on the ORIGINAL message, sends a follow-up to the
        // SAME entity through the broker context (enlisting in the receiver's FullAtomicityViaInfrastructure
        // scope), then records every invocation through the shared registry so the test can observe both the
        // original consumption and the follow-up delivery via Chatter. Registered explicitly into the test DI
        // graph (not via the harness's RecordingMessageHandler wiring) so it can perform the forward.
        private sealed class ForwardingAtomicHandler : IMessageHandler<AtomicCommand>
        {
            private readonly HandlerSignalRegistry _registry;

            public ForwardingAtomicHandler(HandlerSignalRegistry registry)
                => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

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
                        AtomicQueue);
                }
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
                    sb.AddQueueReceiver<AtomicCommand>(AtomicQueue, transactionMode: TransactionMode.FullAtomicityViaInfrastructure);
                    // Register the forwarding handler as the IMessageHandler<AtomicCommand> Chatter resolves on
                    // the receive path (instead of the harness's RecordingMessageHandler), so the handler can
                    // emit the follow-up within the atomic scope.
                    sb.Services.AddTransient<IMessageHandler<AtomicCommand>, ForwardingAtomicHandler>();
                });
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new AtomicCommand { Value = "atomic" }, AtomicQueue);
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
    }
}
