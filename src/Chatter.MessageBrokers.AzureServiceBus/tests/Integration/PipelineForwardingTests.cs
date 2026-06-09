using System;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Context;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Cross-entity forwarding and topic publish/subscribe coverage driven entirely THROUGH Chatter. The
    // SYSTEM UNDER TEST is Chatter's send/forward and publish path:
    //
    //   Forwarding: a command is sent via IBrokeredMessageDispatcher.Send to chatter.forward.source. A handler
    //     on that queue (resolved by Chatter on the receive path) forwards a follow-up to chatter.forward.dest
    //     via the broker context's Send (IMessageBrokerContext.Send). A RecordingMessageHandler on a second
    //     receiver bound to chatter.forward.dest proves the message arrived through Chatter's send path with
    //     its payload intact.
    //
    //   Topic: an event is published via IBrokeredMessageDispatcher.Publish to chatter.topic. A receiver
    //     registered with AddTopicSubscription<TEvt>("chatter.topic","chatter.sub") delivers it to a
    //     RecordingMessageHandler, proving Chatter's topic publish + subscription receive path.
    //
    // No Azure SDK type is used: the send/forward/publish are all Chatter calls and every assertion reads the
    // payload + IMessageBrokerContext Chatter handed the handler.
    //
    // All facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineForwardingTests
    {
        private const string ForwardSourceQueue = "chatter.forward.source";
        private const string ForwardDestQueue = "chatter.forward.dest";
        private const string Topic = "chatter.topic";
        private const string Subscription = "chatter.sub";
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineForwardingTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        // The command consumed on chatter.forward.source. Its handler forwards a ForwardedCommand to
        // chatter.forward.dest through Chatter's broker-context Send.
        public sealed class SourceCommand : ICommand
        {
            public string Value { get; set; }
        }

        // The command the source handler forwards to chatter.forward.dest. A RecordingMessageHandler on the
        // dest receiver captures it, proving the forward landed through Chatter's send path.
        public sealed class ForwardedCommand : ICommand
        {
            public string Value { get; set; }
        }

        // The event published to chatter.topic; delivered to the chatter.sub subscription receiver.
        public sealed class TopicEvent : IEvent
        {
            public string Value { get; set; }
        }

        // A Chatter IMessageHandler<SourceCommand> that, on receipt, forwards a ForwardedCommand to
        // chatter.forward.dest via the broker context's Send — exercising Chatter's send path from inside a
        // handler. Registered explicitly into the test DI graph as the IMessageHandler<SourceCommand> Chatter
        // resolves for the source receiver.
        private sealed class ForwardingSourceHandler : IMessageHandler<SourceCommand>
        {
            public Task Handle(SourceCommand message, IMessageHandlerContext context)
            {
                var brokerContext = context as IMessageBrokerContext;
                brokerContext.Should().NotBeNull(
                    "the source handler runs on Chatter's broker receive path and must receive an IMessageBrokerContext");

                return brokerContext.Send(
                    new ForwardedCommand { Value = message.Value + "-forwarded" },
                    ForwardDestQueue);
            }
        }

        // Forwarding through Chatter: a command sent to chatter.forward.source is consumed by a handler that
        // forwards a follow-up to chatter.forward.dest via the broker context's Send. The dest receiver's
        // RecordingMessageHandler captures the forwarded payload, proving the forward routed through Chatter's
        // send path with the payload intact.
        [RequiresDockerFact]
        public async Task HandlerForwardsToDestinationQueueThroughChatterSendPath()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    // Receiver on the source queue, whose handler forwards via the broker context. Register the
                    // forwarding handler explicitly so Chatter resolves it (instead of a RecordingMessageHandler)
                    // for SourceCommand on the receive path.
                    sb.AddQueueReceiver<SourceCommand>(ForwardSourceQueue);
                    sb.Services.AddTransient<IMessageHandler<SourceCommand>, ForwardingSourceHandler>();

                    // Receiver on the dest queue; its RecordingMessageHandler<ForwardedCommand> (wired by the
                    // harness via the messageTypes arg below) captures the forwarded message.
                    sb.AddQueueReceiver<ForwardedCommand>(ForwardDestQueue);
                },
                typeof(ForwardedCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new SourceCommand { Value = "origin" }, ForwardSourceQueue);
            }

            var handled = await harness.WaitForHandledAsync<ForwardedCommand>(HandlerWait);

            handled.Message.Should().NotBeNull(
                "the forwarded command must be delivered to the dest receiver through Chatter's send path");
            handled.Message.Value.Should().Be(
                "origin-forwarded",
                "the dest handler must receive the exact payload the source handler forwarded");
            handled.Context.Should().NotBeNull(
                "the forwarded message arrives on Chatter's broker receive path with an IMessageBrokerContext");
        }

        // Topic publish/subscribe through Chatter: an event published to chatter.topic via Chatter's
        // dispatcher is delivered to the chatter.sub subscription receiver's RecordingMessageHandler. Proves
        // Chatter's AddTopicSubscription registration + Publish path end-to-end.
        //
        // EMULATOR TOPIC SUPPORT: chatter.topic + chatter.sub are provisioned in Config.json and the official
        // Azure Service Bus emulator supports topics/subscriptions, so this runs for real on the emulator lane.
        // The fact remains [RequiresDockerFact] (skipped without Docker); if a future emulator image drops
        // topic support this will fail loudly rather than silently pass — it is NOT masked by a no-op skip.
        [RequiresDockerFact]
        public async Task PublishedEventIsDeliveredToTopicSubscriptionHandler()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddTopicSubscription<TopicEvent>(Topic, Subscription),
                typeof(TopicEvent));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Publish(new TopicEvent { Value = "published" }, Topic);
            }

            var handled = await harness.WaitForHandledAsync<TopicEvent>(HandlerWait);

            handled.Message.Should().NotBeNull(
                "Chatter's topic publish must deliver the event to the subscription receiver's handler");
            handled.Message.Value.Should().Be(
                "published",
                "the subscription handler must receive the exact published payload");
        }
    }
}
