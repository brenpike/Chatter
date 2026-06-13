using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Canonical end-to-end round-trip proof for the RabbitMQ integration harness. The SYSTEM UNDER TEST is
    // Chatter's own RabbitMQ send + receive path: a command dispatched ONLY through Chatter's
    // IBrokeredMessageDispatcher.Send (harness.SendToQueueAsync / SendWithRoutingAsync) is delivered by the
    // in-process receiver pump (ChatterRabbitMqPipelineHarness) to a Chatter-resolved
    // RecordingMessageHandler<TMessage>, and every assertion reads the payload Chatter deserialized and the
    // IMessageBrokerContext Chatter built — never a raw RabbitMQ.Client type. This exercises the
    // OutboundBrokeredMessage envelope round-trip through RabbitMQ and the inbound header stamps the
    // RabbitMqReceiver applies on receive (InfrastructureType, ReceiveAttempts).
    //
    // Two cases: (a) the default-exchange convention (exchange "" + routing key = queue name); (b) the
    // WithRabbitMqRouting override against an explicitly declared direct exchange + binding.
    //
    // The facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the nightly RabbitMQ CI lane (`--filter Category=Integration`) runs them for
    // real. Mirrors the SQL Service Broker SsbRoundTripTests analogue.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqRoundTripTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqRoundTripTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        // A command carrying string/int/bool fields so the body round-trip exercises Chatter's STJ body
        // converter for each scalar kind (the on-receive materialization must restore the exact CLR values).
        public sealed class RoundTripCommand : ICommand
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public bool Flag { get; set; }
        }

        // Distinct command type for the exchange-routed case so its queue state is independent of the
        // default-exchange case in this collection.
        public sealed class RoutedRoundTripCommand : ICommand
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        // Default-exchange round-trip: a command sent through Chatter's dispatcher to the work-queue name is
        // delivered by Chatter's RabbitMQ pump to the registered handler with its string/int/bool payload
        // deserialized EXACTLY, and the inbound RabbitMqMessageContext stamps the receiver applies are present
        // and correct. Quorum queue so ReceiveAttempts is sourced from the native x-delivery-count (+1).
        [RequiresDockerFact]
        public async Task SentCommandRoundTripsToHandlerOverDefaultExchange()
        {
            var set = RabbitMqTopology.CreateSet("roundtrip", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<RoundTripCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(RoundTripCommand));
            try
            {
                await harness.StartAsync();

                var sent = new RoundTripCommand { Name = "rmq-round-trip", Count = 42, Flag = true };
                await harness.SendToQueueAsync(sent, set.WorkQueueName);

                var handled = await harness.WaitForHandledAsync<RoundTripCommand>(HandlerWait);

                // Payload round-trip: every scalar kind must survive the OutboundBrokeredMessage envelope
                // round-trip through RabbitMQ verbatim.
                handled.Message.Should().NotBeNull("Chatter's RabbitMQ pipeline must deliver the command to the handler");
                handled.Message.Name.Should().Be("rmq-round-trip");
                handled.Message.Count.Should().Be(42);
                handled.Message.Flag.Should().BeTrue();

                // Inbound broker context: the handler must receive an IMessageBrokerContext on the RabbitMQ
                // receive path, carrying the header stamps RabbitMqReceiver applies in ReceiveMessageAsync.
                handled.Context.Should().NotBeNull(
                    "the handler context must be an IMessageBrokerContext on the broker receive path");
                var inbound = handled.Context.BrokeredMessage;

                inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                inbound.MessageContext[MessageContext.InfrastructureType]
                    .Should().Be(RabbitMqMessageContext.InfrastructureType);

                // ReceiveAttempts is sourced from the quorum native x-delivery-count + 1: at least one for a
                // first delivery.
                inbound.MessageContext.Should().ContainKey(MessageContext.ReceiveAttempts);
                Convert.ToInt32(inbound.MessageContext[MessageContext.ReceiveAttempts])
                    .Should().BeGreaterThanOrEqualTo(1, "a delivered message has been received at least once");

                harness.GetSignal<RoundTripCommand>().InvocationCount
                    .Should().Be(1, "the command must be delivered to the handler exactly once on the happy path");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // WithRabbitMqRouting round-trip: a command sent with an explicit direct exchange + routing key is
        // delivered by Chatter's pump to the work queue bound under that key, proving the sender's
        // ResolveAddress override path (exchange != "" / routing key != Destination) reaches the bound queue.
        [RequiresDockerFact]
        public async Task SentCommandRoundTripsToHandlerOverExplicitExchangeRouting()
        {
            const string exchange = "chatter_rmq_it_roundtrip_exchange";
            const string routingKey = "chatter.rmq.it.roundtrip.key";

            var set = RabbitMqTopology.CreateSet("routed", QueueType.Quorum, exchange: exchange, routingKey: routingKey);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<RoutedRoundTripCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(RoutedRoundTripCommand));
            try
            {
                await harness.StartAsync();

                var sent = new RoutedRoundTripCommand { Name = "rmq-routed", Count = 7 };
                // destinationPath is still the work-queue name (the dispatcher needs a Destination), but the
                // explicit exchange + routing key override addressing so the publish goes through the declared
                // exchange and binding rather than the default exchange.
                await harness.SendWithRoutingAsync(sent, set.WorkQueueName, exchange, routingKey);

                var handled = await harness.WaitForHandledAsync<RoutedRoundTripCommand>(HandlerWait);

                handled.Message.Should().NotBeNull(
                    "the explicit exchange + routing key must route the command through the declared binding to the work queue");
                handled.Message.Name.Should().Be("rmq-routed");
                handled.Message.Count.Should().Be(7);

                var inbound = handled.Context.BrokeredMessage;
                inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                inbound.MessageContext[MessageContext.InfrastructureType]
                    .Should().Be(RabbitMqMessageContext.InfrastructureType);
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
    }
}
