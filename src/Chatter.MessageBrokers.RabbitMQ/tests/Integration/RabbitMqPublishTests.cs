using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Core-behavior C2 proof for the RabbitMQ adapter: an IEvent published ONLY through Chatter's
    // IBrokeredMessageDispatcher.Publish path (harness.PublishToQueueAsync) is delivered by the in-process
    // receiver pump (ChatterRabbitMqPipelineHarness) to a Chatter-resolved RecordingMessageHandler<TEvent>, and
    // every assertion reads the payload Chatter deserialized and the IMessageBrokerContext Chatter built — never a
    // raw RabbitMQ.Client type. This is the publish/event analogue of the command Send round-trip in
    // RabbitMqRoundTripTests, exercising the dispatcher's Publish (IEvent) overload rather than Send (ICommand).
    //
    // Gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain `dotnet test`
    // stays green; the nightly RabbitMQ CI lane (`--filter Category=Integration`) runs it for real.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqPublishTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqPublishTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        // An event carrying string/int fields so the body round-trip exercises Chatter's STJ body converter for the
        // publish path (the on-receive materialization must restore the exact CLR values).
        public sealed class TestIntegrationEvent : IEvent
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        // Default-exchange publish round-trip: an event PUBLISHED through Chatter's dispatcher to the work-queue
        // name is delivered by Chatter's RabbitMQ pump to the registered handler exactly once with its string/int
        // payload deserialized EXACTLY, and the inbound RabbitMqMessageContext InfrastructureType stamp is present.
        [RequiresDockerFact]
        public async Task PublishedEventRoundTripsToHandlerOverDefaultExchange()
        {
            var set = RabbitMqTopology.CreateSet("publish", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<TestIntegrationEvent>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(TestIntegrationEvent));
            try
            {
                await harness.StartAsync();

                var published = new TestIntegrationEvent { Name = "rmq-publish", Count = 17 };
                await harness.PublishToQueueAsync(published, set.WorkQueueName);

                var handled = await harness.WaitForHandledAsync<TestIntegrationEvent>(HandlerWait);

                // Payload round-trip: the published event must survive the OutboundBrokeredMessage envelope
                // round-trip through RabbitMQ verbatim.
                handled.Message.Should().NotBeNull("Chatter's RabbitMQ pipeline must deliver the published event to the handler");
                handled.Message.Name.Should().Be("rmq-publish");
                handled.Message.Count.Should().Be(17);

                // Inbound broker context: the handler must receive an IMessageBrokerContext on the RabbitMQ receive
                // path, carrying the RabbitMQ InfrastructureType stamp the receiver applies in ReceiveMessageAsync.
                handled.Context.Should().NotBeNull(
                    "the handler context must be an IMessageBrokerContext on the broker receive path");
                var inbound = handled.Context.BrokeredMessage;

                inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                inbound.MessageContext[MessageContext.InfrastructureType]
                    .Should().Be(RabbitMqMessageContext.InfrastructureType);

                harness.GetSignal<TestIntegrationEvent>().InvocationCount
                    .Should().Be(1, "the event must be delivered to the handler exactly once on the happy path");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
    }
}
