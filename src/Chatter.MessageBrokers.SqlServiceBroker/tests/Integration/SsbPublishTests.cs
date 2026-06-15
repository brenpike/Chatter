using System;
using System.Threading.Tasks;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Core-behavior C2 proof for the SQL Service Broker adapter (STEP-006): an IEvent PUBLISHED through Chatter
    // (IBrokeredMessageDispatcher.Publish, via harness.PublishAsync) is delivered by Chatter's in-process SSB
    // receiver pump to a Chatter-resolved RecordingMessageHandler<TEvent> exactly once, with its payload intact.
    // The SYSTEM UNDER TEST is Chatter's own SSB publish + receive path; every assertion reads the event Chatter
    // deserialized and the IMessageBrokerContext Chatter built — never a raw SSB / ADO.NET type. This is the
    // publish-side analogue of SsbRoundTripTests (which proves the command Send path) and confirms the receiver
    // graph and AddQueueReceiver<TEvent> registration accept an IEvent type cleanly.
    //
    // C4 (per-message content-type selection) is N/A for SSB and intentionally NOT tested: the SSB sender selects
    // its body converter from the fixed _options.MessageBodyType, never from a per-message ContentType (confirmed
    // STEP-005).
    //
    // The fact is gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the nightly SQL Server CI lane (`--filter Category=Integration`) runs it for real.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbPublishTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbPublishTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        // An event carrying string/int fields so the publish body round-trip exercises Chatter's STJ body
        // converter and the on-receive materialization restores the exact CLR values. Declared a class implementing
        // IEvent so it satisfies AddQueueReceiver<TMessage>'s `where TMessage : class, IMessage` constraint
        // (IEvent : IMessage) and PublishAsync's `where TEvent : class, IEvent` constraint.
        public sealed class TestIntegrationEvent : IEvent
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        private ChatterSsbPipelineHarness BuildHarness()
            => ChatterSsbPipelineHarness.Build(
                _fixture.GetAppConnectionString(),
                ServiceBrokerProvisioning.PublishSet,
                ssb => ssb.AddQueueReceiver<TestIntegrationEvent>(
                    ServiceBrokerProvisioning.PublishSet.TargetQueuePathBracketed,
                    deadLetterServicePath: ServiceBrokerProvisioning.PublishSet.DeadLetterServiceName),
                typeof(TestIntegrationEvent));

        // C2: an event published through Chatter's dispatcher is delivered by Chatter's SSB pump to the registered
        // handler exactly once, with its string/int payload deserialized EXACTLY, and the inbound SSBMessageContext
        // carries the SSB InfrastructureType stamp. The post-dispatch EndDialog system message is auto-acked by the
        // receiver's classifier and must NOT reach the handler, so the handler is invoked exactly once.
        [RequiresDockerFact]
        public async Task PublishedEventReachesHandlerExactlyOnceWithPayloadIntact()
        {
            var harness = BuildHarness();
            try
            {
                await harness.StartAsync();

                var published = new TestIntegrationEvent { Name = "ssb-publish", Count = 7 };
                await harness.PublishAsync(published);

                var handled = await harness.WaitForHandledAsync<TestIntegrationEvent>(HandlerWait);

                // Payload round-trip: the published event must survive the OutboundBrokeredMessage envelope
                // round-trip through SQL Service Broker verbatim.
                handled.Message.Should().NotBeNull("Chatter's SSB publish pipeline must deliver the event to the handler");
                handled.Message.Name.Should().Be("ssb-publish");
                handled.Message.Count.Should().Be(7);

                // Inbound broker context: the handler must receive an IMessageBrokerContext on the SSB receive path,
                // carrying the InfrastructureType stamp that identifies the SSB receiver.
                handled.Context.Should().NotBeNull(
                    "the handler context must be an IMessageBrokerContext on the broker receive path");
                var inbound = handled.Context.BrokeredMessage;

                inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                inbound.MessageContext[MessageContext.InfrastructureType]
                    .Should().Be(SSBMessageContext.InfrastructureType);

                // Exactly-once: the post-dispatch EndDialog system message (EndConversationAfterDispatch default
                // true) is auto-acked by the receiver's classifier and never reaches the handler, so the handler is
                // invoked exactly once — for the published event alone.
                harness.GetSignal<TestIntegrationEvent>().InvocationCount
                    .Should().Be(1, "the EndDialog system message is auto-acked and must not reach the handler");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
    }
}
