using System;
using System.Text;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
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
    // Cross-entity forwarding and topic publish/subscribe coverage driven THROUGH Chatter. The SYSTEM UNDER
    // TEST is Chatter's send/forward and publish path:
    //
    //   Forwarding: a command is sent via IBrokeredMessageDispatcher.Send to chatter.forward.source. A handler
    //     on that queue (resolved by Chatter on the receive path) forwards a follow-up to chatter.forward.dest
    //     via the broker context's Send (IMessageBrokerContext.Send). The forwarded message is then observed on
    //     chatter.forward.dest by an edge-only raw-SDK receive, proving the forward routed through Chatter's
    //     send path with its payload intact.
    //
    //     TOPOLOGY CONSTRAINT (why the dest is read at the edge, not via a second Chatter receiver): the
    //     production ServiceBusClient is built with EnableCrossEntityTransactions = true
    //     (ChatterAzureServiceBusExtensions.CreateSharedClient) and ALL receivers/senders are created off that
    //     one shared client. With cross-entity transactions enabled, the Azure SDK pins the first entity the
    //     client touches as the transaction "via" entity and REJECTS a second receiver bound to a different
    //     top-level entity on the same client — "Local transactions cannot span multiple top-level entities
    //     such as queue or topic" (Azure/azure-sdk-for-net#34997). So a second in-process Chatter receiver on
    //     chatter.forward.dest cannot run alongside the chatter.forward.source receiver; its receive throws and
    //     BrokeredMessageReceiver.StartReceiver swallows the failure (logs LogCritical, does not rethrow), so
    //     the handler is simply never invoked. The forward itself succeeds (Chatter's send path runs under a
    //     ReceiveOnly Suppress scope), so the delivered message is observed at the dest with a raw-SDK edge
    //     receive instead of a second Chatter pump. See the suspected-production-concern note in the worker
    //     report.
    //
    //   Topic: an event is published via IBrokeredMessageDispatcher.Publish to chatter.topic. A receiver
    //     registered with AddTopicSubscription<TEvt>("chatter.topic","chatter.sub") delivers it to a
    //     RecordingMessageHandler, proving Chatter's topic publish + subscription receive path.
    //
    // Raw Azure.Messaging.ServiceBus appears ONLY at the test EDGE to receive the forwarded message from
    // chatter.forward.dest (mirroring the deadletter tests' edge peek of $DeadLetterQueue) — never as the
    // system under test. The send/forward/publish are all Chatter calls.
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
        // Generous on purpose: the forwarding chain (source-receive -> handler -> forward-send -> dest-enqueue
        // -> edge-receive) runs against the slow emulator, where a full integration run takes minutes. 30s was
        // tight for this two-hop path; 90s gives headroom without masking a real wiring failure (the topology
        // fix, not the timeout, is the actual correction for the prior hang).
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(90);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineForwardingTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        // The command consumed on chatter.forward.source. Its handler forwards a ForwardedCommand to
        // chatter.forward.dest through Chatter's broker-context Send.
        public sealed class SourceCommand : ICommand
        {
            public string Value { get; set; }
        }

        // The command the source handler forwards to chatter.forward.dest. An edge-only raw-SDK receive on the
        // dest queue captures it, proving the forward landed through Chatter's send path.
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

        // Edge-only raw-SDK receive of the forwarded message from chatter.forward.dest. Chatter cannot run a
        // second in-process receiver on this entity alongside the chatter.forward.source receiver (the shared
        // EnableCrossEntityTransactions client rejects a second receiver on a different top-level entity — see
        // the class-level TOPOLOGY CONSTRAINT note), so the Azure SDK is used here purely to observe the message
        // Chatter's forward produced. PeekLock + explicit Complete drains it so a leftover cannot leak into a
        // later run on the shared emulator. The body is the UTF-8 JSON Chatter's JsonBodyConverter wrote, so the
        // forwarded payload is asserted by decoding the raw body rather than re-running Chatter's deserializer.
        private async Task<string> ReceiveForwardedBodyAsync(TimeSpan timeout)
        {
            await using var client = new ServiceBusClient(_emulator.GetConnectionString());
            var receiver = client.CreateReceiver(
                ForwardDestQueue,
                new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

            var forwarded = await receiver.ReceiveMessageAsync(timeout);
            if (forwarded is null)
            {
                return null;
            }

            await receiver.CompleteMessageAsync(forwarded);
            return Encoding.UTF8.GetString(forwarded.Body.ToArray());
        }

        // Forwarding through Chatter: a command sent to chatter.forward.source is consumed by a handler that
        // forwards a follow-up to chatter.forward.dest via the broker context's Send. The forwarded message is
        // observed on chatter.forward.dest by an edge-only raw-SDK receive, proving the forward routed through
        // Chatter's send path with the payload intact. Only the source receiver runs in-process; the dest is
        // read at the edge because the shared cross-entity-transaction client cannot host a second receiver on a
        // different top-level entity (class-level TOPOLOGY CONSTRAINT note).
        [RequiresDockerFact]
        public async Task HandlerForwardsToDestinationQueueThroughChatterSendPath()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb =>
                {
                    // Receiver on the source queue, whose handler forwards via the broker context. Register the
                    // forwarding handler explicitly so Chatter resolves it (instead of a RecordingMessageHandler)
                    // for SourceCommand on the receive path. NO dest receiver: a second receiver on
                    // chatter.forward.dest would be rejected by the shared EnableCrossEntityTransactions client.
                    sb.AddQueueReceiver<SourceCommand>(ForwardSourceQueue);
                    sb.Services.AddTransient<IMessageHandler<SourceCommand>, ForwardingSourceHandler>();
                });
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new SourceCommand { Value = "origin" }, ForwardSourceQueue);
            }

            // The source handler's forward (Chatter's send path) must land the ForwardedCommand on
            // chatter.forward.dest; observe it at the edge. The wait is generous because the emulator must
            // deliver the source message, run the handler, complete the forward send, then enqueue on the dest.
            var forwardedBody = await ReceiveForwardedBodyAsync(HandlerWait);

            forwardedBody.Should().NotBeNull(
                "the forwarded command must be delivered to chatter.forward.dest through Chatter's send path");
            forwardedBody.Should().Contain(
                "origin-forwarded",
                "the dest queue must hold the exact payload the source handler forwarded");
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
