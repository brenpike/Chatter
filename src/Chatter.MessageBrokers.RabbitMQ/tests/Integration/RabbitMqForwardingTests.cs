using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Proves RabbitMQ honors core behavior C10 (in-handler forward) end-to-end through Chatter's own send path: a
    // command delivered to a handler on a SOURCE queue calls IMessageBrokerContext.Send to a SECOND (destination)
    // queue, and a Chatter receiver on that destination queue delivers the forwarded payload to its handler. The
    // forward is a Chatter call (the broker context's Send, NOT a raw RabbitMQ.Client publish), and the forwarded
    // message is observed via a second Chatter receiver's RecordingMessageHandler<ForwardedCommand> — the whole
    // path is Chatter's. Mirrors the Azure Service Bus PipelineForwardingTests analogue.
    //
    // TOPOLOGY NOTE (why TWO harness instances, not one): the RabbitMQ adapter rejects MORE THAN ONE RabbitMQ
    // receiver per process at registration (Extensions.RejectMultipleReceivers throws NotSupportedException — the
    // singleton connection source owns one receive channel). So the source receiver and the destination receiver
    // CANNOT share a host. Each harness instance builds its OWN ServiceProvider with exactly ONE RabbitMQ receiver
    // (satisfying RejectMultipleReceivers), and both target the SAME RabbitMqFixture broker via the SAME AMQP URI,
    // so the forward published by harness A's source handler lands on the queue harness B consumes. Both receivers
    // are ReceiveOnly (no FullAtomicity, which the adapter also rejects on RabbitMQ).
    //
    // Gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain `dotnet test`
    // stays green; the nightly RabbitMQ CI lane (`--filter Category=Integration`) runs it for real.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqForwardingTests
    {
        // Generous on purpose: the forwarding chain (source-receive -> handler -> forward-send through Chatter ->
        // dest-enqueue -> dest-receive) crosses two in-process Chatter hosts against a real broker. 30s gives
        // headroom for the two-hop path without masking a real wiring failure.
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqForwardingTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        // The command consumed on the source queue. Its handler forwards a ForwardedCommand to the destination
        // queue through Chatter's broker-context Send.
        public sealed class SourceCommand : ICommand
        {
            public string Value { get; set; }
        }

        // The command the source handler forwards to the destination queue. A second in-process Chatter receiver
        // (harness B) delivers it to a RecordingMessageHandler, proving the forward landed through Chatter's send
        // path with its payload intact.
        public sealed class ForwardedCommand : ICommand
        {
            public string Value { get; set; }
        }

        // A Chatter IMessageHandler<SourceCommand> that, on receipt, forwards a ForwardedCommand to the destination
        // queue via the broker context's Send — exercising Chatter's send path from inside a handler. No explicit
        // InfrastructureType is stamped: RabbitMQ is the only (default) infrastructure in the source harness, so
        // the dispatcher routes the forward to the RabbitMQ sender. The destination queue name is supplied at
        // construction so the test can mint a per-scenario queue. Registered explicitly into the source harness DI
        // graph as the IMessageHandler<SourceCommand> Chatter resolves on the source receive path.
        private sealed class ForwardingSourceHandler : IMessageHandler<SourceCommand>
        {
            private readonly string _destinationQueueName;

            public ForwardingSourceHandler(string destinationQueueName)
                => _destinationQueueName = destinationQueueName;

            public Task Handle(SourceCommand message, IMessageHandlerContext context)
            {
                var brokerContext = context as IMessageBrokerContext;
                brokerContext.Should().NotBeNull(
                    "the source handler runs on Chatter's broker receive path and must receive an IMessageBrokerContext");

                return brokerContext.Send(
                    new ForwardedCommand { Value = message.Value + "-forwarded" },
                    _destinationQueueName);
            }
        }

        // Forwarding through Chatter on RabbitMQ (C10): a command sent to the source queue is consumed by harness
        // A's handler, which forwards a follow-up to the destination queue via the broker context's Send. Harness
        // B's receiver on the destination queue delivers the forwarded command to its RecordingMessageHandler,
        // proving the forward routed through Chatter's send path with the payload intact — end to end across two
        // single-receiver hosts that share the broker.
        [RequiresDockerFact]
        public async Task HandlerForwardsToDestinationQueueThroughChatterSendPath()
        {
            var amqpUri = _fixture.GetAmqpConnectionString();

            // Distinct per-scenario queue sets so the source and destination queues never share state. Declare both
            // against the shared broker BEFORE either receiver starts — the adapter provisions no topology.
            var sourceSet = RabbitMqTopology.CreateSet("forward_source", QueueType.Quorum);
            var destSet = RabbitMqTopology.CreateSet("forward_dest", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(amqpUri, sourceSet, CancellationToken.None);
            await RabbitMqTopology.DeclareAsync(amqpUri, destSet, CancellationToken.None);

            // Harness A: hosts ONLY the source receiver. The forwarding handler is registered explicitly via
            // configureServices as the IMessageHandler<SourceCommand> Chatter resolves on the source receive path
            // (SourceCommand is deliberately NOT passed as a recording message type, so the forwarding handler is
            // the sole registration). ReceiveOnly — no FullAtomicity, which the adapter rejects on RabbitMQ.
            var sourceHarness = ChatterRabbitMqPipelineHarness.Build(
                amqpUri,
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<SourceCommand>(
                    sourceSet.WorkQueueName,
                    transactionMode: TransactionMode.ReceiveOnly,
                    deadLetterQueuePath: sourceSet.DeadLetterQueueName),
                services => services.AddTransient<IMessageHandler<SourceCommand>>(
                    _ => new ForwardingSourceHandler(destSet.WorkQueueName)));

            // Harness B: hosts ONLY the destination receiver, recording the forwarded command. Its own
            // ServiceProvider means it registers exactly one RabbitMQ receiver, satisfying RejectMultipleReceivers.
            // Same AMQP URI as harness A, so it consumes the queue harness A's handler forwards to.
            var destHarness = ChatterRabbitMqPipelineHarness.Build(
                amqpUri,
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<ForwardedCommand>(
                    destSet.WorkQueueName,
                    transactionMode: TransactionMode.ReceiveOnly,
                    deadLetterQueuePath: destSet.DeadLetterQueueName),
                typeof(ForwardedCommand));

            try
            {
                // Start the destination receiver first so it is consuming before the forward is published, then
                // start the source receiver that drives the forward.
                await destHarness.StartAsync();
                await sourceHarness.StartAsync();

                // Drive the chain: send a SourceCommand to the source queue via harness A's dispatcher. RabbitMQ is
                // the only broker, so SendToQueueAsync stamps the RabbitMQ InfrastructureType and the in-process
                // source pump delivers it to the forwarding handler.
                var sent = new SourceCommand { Value = "origin" };
                await sourceHarness.SendToQueueAsync(sent, sourceSet.WorkQueueName);

                // The source handler's forward (Chatter's send path) must land the ForwardedCommand on the
                // destination queue, where harness B's receiver delivers it to its RecordingMessageHandler.
                var handled = await destHarness.WaitForHandledAsync<ForwardedCommand>(HandlerWait);

                handled.Message.Should().NotBeNull(
                    "the forwarded command must be delivered to the destination queue through Chatter's send path");
                handled.Message.Value.Should().Be(
                    "origin-forwarded",
                    "the destination receiver's handler must receive the exact payload the source handler forwarded");

                // The forwarded delivery arrived on Chatter's RabbitMQ receive path: the inbound context must carry
                // the RabbitMQ InfrastructureType stamp the receiver applies.
                handled.Context.Should().NotBeNull(
                    "the destination handler context must be an IMessageBrokerContext on the broker receive path");
                var inbound = handled.Context.BrokeredMessage;
                inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                inbound.MessageContext[MessageContext.InfrastructureType]
                    .Should().Be(RabbitMqMessageContext.InfrastructureType);

                destHarness.GetSignal<ForwardedCommand>().InvocationCount
                    .Should().Be(1, "the forwarded command must be delivered to the destination handler exactly once");
            }
            finally
            {
                // Dispose both hosts so consumers/connections tear down cleanly. Dispose the source first so it
                // stops forwarding before the destination receiver is torn down.
                await sourceHarness.DisposeAsync();
                await destHarness.DisposeAsync();
            }
        }
    }
}
