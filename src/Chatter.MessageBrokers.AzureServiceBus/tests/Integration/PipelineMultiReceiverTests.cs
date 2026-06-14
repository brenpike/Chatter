using System;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Multi-receiver host coverage: TWO queue receivers bound to DISTINCT top-level entities run in ONE
    // in-process host through Chatter's real DI graph and receiver pump, and BOTH handlers are invoked. This
    // is the EXACT scenario that hung on v1.0.0 — the shared ServiceBusClient was always built with
    // EnableCrossEntityTransactions, so the Azure SDK pinned the first entity the client touched as the
    // transaction "via" entity and rejected a second receiver on a different top-level entity, which
    // BrokeredMessageReceiver.StartReceiver swallowed (the handler was simply never invoked).
    //
    // After the opt-in fix (cross-entity transactions default OFF, auto-enabled only when a
    // FullAtomicityViaInfrastructure receiver is registered) a host with multiple non-atomic receivers on
    // distinct entities no longer trips the SDK's single-via-entity rule, so both pumps deliver. Both
    // receivers here use TransactionMode.ReceiveOnly (NOT FullAtomicity), so cross-entity stays OFF and each
    // handled message is settled (Complete) and does not leak onto the shared emulator's queues.
    //
    // The DI-time startup guard (cross-entity ON + multiple distinct top-level entities throws at client
    // build) is exercised WITHOUT a broker by the WhenConfiguringCrossEntityTransactions unit facts, so it is
    // deliberately NOT re-covered here as a Docker-gated test.
    //
    // All facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineMultiReceiverTests
    {
        // Two DISTINCT top-level queue entities dedicated to this class — one per receiver — so no other test
        // class can send to or receive from these queues concurrently.
        private const string QueueA = "chatter.multireceiver.a";
        private const string QueueB = "chatter.multireceiver.b";

        // Generous on purpose: two receivers must each deliver against the slow emulator, where a full
        // integration run takes minutes. 90s per handler gives headroom without masking a real wiring failure
        // (the multi-receiver hang is the scenario under test, so a stall must fail fast via TimeoutException
        // rather than hang CI).
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(90);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineMultiReceiverTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        public sealed class AlphaCommand : ICommand
        {
            public string Value { get; set; }
        }

        public sealed class BetaCommand : ICommand
        {
            public string Value { get; set; }
        }

        // Two queue receivers on distinct top-level entities, registered in ONE host via two AddQueueReceiver
        // calls in the same configure delegate. A message sent to each must reach its own handler with the
        // exact payload — proving multiple receivers on distinct entities now coexist in one host (the prior
        // cross-entity-transaction hang is gone).
        [RequiresDockerFact]
        public async Task MultipleReceiversOnDistinctEntitiesEachDeliverToTheirHandler()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    sb.AddQueueReceiver<AlphaCommand>(QueueA, transactionMode: TransactionMode.ReceiveOnly);
                    sb.AddQueueReceiver<BetaCommand>(QueueB, transactionMode: TransactionMode.ReceiveOnly);
                },
                typeof(AlphaCommand),
                typeof(BetaCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new AlphaCommand { Value = "alpha" }, QueueA);
                await dispatcher.Send(new BetaCommand { Value = "beta" }, QueueB);
            }

            var alphaHandled = await harness.WaitForHandledAsync<AlphaCommand>(HandlerWait);
            var betaHandled = await harness.WaitForHandledAsync<BetaCommand>(HandlerWait);

            alphaHandled.Message.Should().NotBeNull(
                "the receiver on the first entity must deliver its command to its handler");
            alphaHandled.Message.Value.Should().Be(
                "alpha",
                "the first handler must receive the exact payload sent to the first entity");

            betaHandled.Message.Should().NotBeNull(
                "the receiver on the second entity must deliver its command to its handler in the same host");
            betaHandled.Message.Value.Should().Be(
                "beta",
                "the second handler must receive the exact payload sent to the second entity");
        }
    }
}
