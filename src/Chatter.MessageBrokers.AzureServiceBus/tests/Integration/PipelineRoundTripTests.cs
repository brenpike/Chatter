using System;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Routing.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // End-to-end round-trip coverage of Chatter's Azure Service Bus pipeline. The SYSTEM UNDER TEST is
    // Chatter's own send + receive path: a command is dispatched ONLY through Chatter's
    // IBrokeredMessageDispatcher.Send, the in-process receiver pump (ChatterPipelineHarness) delivers it to a
    // Chatter-resolved RecordingMessageHandler<TMessage>, and every assertion reads the payload Chatter
    // deserialized and the IMessageBrokerContext Chatter built — never an Azure SDK type. This exercises
    // Chatter's outbound MessageContext -> ApplicationProperties mapping, the System.Text.Json body
    // round-trip, and the inbound InboundBrokeredMessageFactory header stamps (InfrastructureType,
    // ReceiveAttempts, TTL/expiry) plus correlation-id propagation.
    //
    // All facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineRoundTripTests
    {
        private const string RoundTripQueue = "chatter.roundtrip";
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineRoundTripTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        // A command carrying string/int/bool fields so the body round-trip exercises Chatter's STJ body
        // converter for each scalar kind (the on-receive materialization must restore the exact CLR values).
        public sealed class RoundTripCommand : ICommand
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public bool Flag { get; set; }
        }

        private ChatterPipelineHarness BuildHarness()
            => ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<RoundTripCommand>(RoundTripQueue),
                typeof(RoundTripCommand));

        // Round-trip: a command sent through Chatter's dispatcher is delivered by Chatter's pump to the
        // registered handler with its string/int/bool payload deserialized exactly. Asserts purely on the
        // payload Chatter handed the handler.
        [RequiresDockerFact]
        public async Task SentCommandIsDeliveredToHandlerWithDeserializedPayload()
        {
            await using var harness = BuildHarness();
            await harness.StartAsync();

            var sent = new RoundTripCommand { Name = "round-trip", Count = 42, Flag = true };

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(sent, RoundTripQueue);
            }

            var handled = await harness.WaitForHandledAsync<RoundTripCommand>(HandlerWait);

            handled.Message.Should().NotBeNull("Chatter's pipeline must deliver the command to the handler");
            handled.Message.Name.Should().Be("round-trip");
            handled.Message.Count.Should().Be(42);
            handled.Message.Flag.Should().BeTrue();
        }

        // Body serialization round-trip through Chatter's STJ serializer: a payload mixing bool + numeric +
        // string fields must survive verbatim, proving the send-side Convert and the receive-side
        // GetMessageFromBody materialization agree on every scalar kind.
        [RequiresDockerFact]
        public async Task BodyScalarsRoundTripExactlyThroughChatterSerializer()
        {
            await using var harness = BuildHarness();
            await harness.StartAsync();

            var sent = new RoundTripCommand { Name = "scalars", Count = -7, Flag = false };

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(sent, RoundTripQueue);
            }

            var handled = await harness.WaitForHandledAsync<RoundTripCommand>(HandlerWait);

            handled.Message.Name.Should().Be("scalars");
            handled.Message.Count.Should().Be(-7, "a negative integer must round-trip through STJ unchanged");
            handled.Message.Flag.Should().BeFalse("a false bool must round-trip through STJ unchanged");
        }

        // Header / application-property / correlation-id propagation: a custom application property and an
        // explicit correlation id set on SendOptions must survive the round trip and be readable from the
        // handler's IMessageBrokerContext, alongside the inbound header stamps Chatter applies on receive
        // (InfrastructureType, ReceiveAttempts, TTL, expiry, message id).
        [RequiresDockerFact]
        public async Task HeadersCorrelationAndStampsPropagateToHandlerContext()
        {
            await using var harness = BuildHarness();
            await harness.StartAsync();

            const string customPropertyKey = "X-Test-Property";
            const string customPropertyValue = "custom-value";
            var correlationId = Guid.NewGuid().ToString();

            var options = new SendOptions();
            options.SetCorrelationId(correlationId);
            options.WithMessageContext(customPropertyKey, customPropertyValue);

            var sent = new RoundTripCommand { Name = "with-headers", Count = 1, Flag = true };

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(sent, RoundTripQueue, options: options);
            }

            var handled = await harness.WaitForHandledAsync<RoundTripCommand>(HandlerWait);

            handled.Context.Should().NotBeNull(
                "the handler context must be an IMessageBrokerContext on the broker receive path");
            var inbound = handled.Context.BrokeredMessage;

            // Correlation id set via SendOptions survives the round trip and is exposed by Chatter's inbound
            // message (sourced from the Chatter.CorrelationId application property).
            inbound.CorrelationId.Should().Be(correlationId);

            // The custom application property survives as an inbound application property.
            inbound.MessageContext.Should().ContainKey(customPropertyKey);
            inbound.MessageContext[customPropertyKey].Should().Be(customPropertyValue);

            // Message id is assigned by Chatter's outbound path and surfaced on the inbound message.
            inbound.MessageId.Should().NotBeNullOrWhiteSpace();

            // Inbound header stamps applied by Chatter's InboundBrokeredMessageFactory on receive.
            inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
            inbound.MessageContext[MessageContext.InfrastructureType].Should().Be(ASBMessageContext.InfrastructureType);

            inbound.MessageContext.Should().ContainKey(MessageContext.ReceiveAttempts);
            Convert.ToInt32(inbound.MessageContext[MessageContext.ReceiveAttempts])
                .Should().BeGreaterThanOrEqualTo(1, "a delivered message has been received at least once");

            inbound.MessageContext.Should().ContainKey(MessageContext.TimeToLive);
            inbound.MessageContext[MessageContext.TimeToLive].Should().BeOfType<TimeSpan>();

            inbound.MessageContext.Should().ContainKey(MessageContext.ExpiryTimeUtc);
            inbound.MessageContext[MessageContext.ExpiryTimeUtc].Should().BeOfType<DateTime>();
        }
    }
}
