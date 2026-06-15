using System;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Core-behavior C10 proof for the SQL Service Broker adapter (STEP-008): in-handler forward. The SYSTEM
    // UNDER TEST is Chatter's own SSB send/forward + receive path. A command is sent through Chatter
    // (IBrokeredMessageDispatcher.Send, via harness.SendAsync) to the ForwardingSet source service; a handler
    // on that source service — resolved by Chatter on the receive path — forwards a follow-up to a SECOND,
    // DISTINCT destination service (ServiceBrokerProvisioning.ForwardDestinationServiceName) via the broker
    // context's Send (IMessageBrokerContext.Send). A SECOND in-process Chatter receiver on the destination
    // service+queue then delivers the forwarded command to its RecordingMessageHandler<ForwardedCommand>,
    // proving the forward routed through Chatter's SSB send path with its payload intact — end to end, with no
    // raw SSB / ADO.NET edge. This is the SSB analogue of the Azure Service Bus PipelineForwardingTests
    // forwarding scenario.
    //
    // TWO RECEIVERS IN ONE HOST: SSB permits multiple receivers in one host (unlike the single-receiver
    // RabbitMQ host), so both the source receiver (AddQueueReceiver<SourceCommand> on the ForwardingSet target
    // queue) and the destination receiver (AddQueueReceiver<ForwardedCommand> on the ForwardDestination queue)
    // are registered together in the harness's configureReceivers delegate. The harness's single owning
    // _objectSet (ForwardingSet) only governs the DEFAULT SendAsync target service; receiver registration is
    // free-form in the delegate, so no harness change is needed to host the second receiver on a different
    // service.
    //
    // SSB FORWARD STAMPS: the harness's SendAsync stamps the SSB initiator/contract/message-type headers the
    // SqlServiceBrokerSender reads to BEGIN DIALOG / SEND. A handler-driven brokerContext.Send does NOT inherit
    // those stamps automatically, so the forwarding handler stamps the SAME SSB headers (InitiatorServiceName,
    // ContractName, MessageTypeName) on its SendOptions, mirroring the harness's CreateSsbSendOptions, and sends
    // to the BARE ForwardDestinationServiceName (BeginDialogConversationCommand strips brackets and uses it as
    // "TO SERVICE", so the destination must name the SERVICE, not the queue).
    //
    // The fact is gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the nightly SQL Server CI lane (`--filter Category=Integration`) runs it for real.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbForwardingTests
    {
        // Generous on purpose: the forwarding chain (source-receive -> handler -> forward-send via BEGIN DIALOG /
        // SEND -> dest-enqueue -> dest-receive) is a two-hop SSB path. 30 s matches the other SSB integration
        // waits and gives headroom without masking a real wiring failure.
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbForwardingTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        // The command consumed on the ForwardingSet source service. Its handler forwards a ForwardedCommand to
        // the distinct ForwardDestination service through Chatter's broker-context Send.
        public sealed class SourceCommand : ICommand
        {
            public string Value { get; set; }
        }

        // The command the source handler forwards to the ForwardDestination service. A second in-process Chatter
        // receiver on the destination delivers it to a RecordingMessageHandler, proving the forward landed
        // through Chatter's SSB send path.
        public sealed class ForwardedCommand : ICommand
        {
            public string Value { get; set; }
        }

        // A Chatter IMessageHandler<SourceCommand> that, on receipt, forwards a ForwardedCommand to the distinct
        // ForwardDestination service via the broker context's Send — exercising Chatter's SSB send path from
        // inside a handler. Mirrors the Azure Service Bus ForwardingSourceHandler, with the SSB difference that
        // it must stamp the SSB initiator/contract/message-type headers on the SendOptions (a handler-driven Send
        // does not inherit the harness's stamps) so the SqlServiceBrokerSender can BEGIN DIALOG / SEND. Registered
        // explicitly into the test DI graph as the IMessageHandler<SourceCommand> Chatter resolves for the source
        // receiver (instead of a RecordingMessageHandler).
        private sealed class ForwardingSourceHandler : IMessageHandler<SourceCommand>
        {
            public Task Handle(SourceCommand message, IMessageHandlerContext context)
            {
                var brokerContext = context as IMessageBrokerContext;
                brokerContext.Should().NotBeNull(
                    "the source handler runs on Chatter's SSB broker receive path and must receive an IMessageBrokerContext");

                // Stamp the SSB headers the SqlServiceBrokerSender reads to BEGIN DIALOG / SEND on the shared
                // initiator service + //Chatter contract + //Chatter/BrokeredMessage message type, mirroring the
                // harness's CreateSsbSendOptions. Without these the handler-driven Send cannot route through SSB.
                var options = new SendOptions();
                options.WithMessageContext(SSBMessageContext.ServiceName, ServiceBrokerProvisioning.InitiatorServiceName);
                options.WithMessageContext(SSBMessageContext.ServiceContractName, ServiceBrokerProvisioning.ContractName);
                options.WithMessageContext(SSBMessageContext.MessageTypeName, ServiceBrokerProvisioning.MessageTypeName);

                // destinationPath is the BARE destination SERVICE name: BeginDialogConversationCommand strips
                // brackets and uses it as "TO SERVICE", so it must name the service, not the queue.
                return brokerContext.Send(
                    new ForwardedCommand { Value = message.Value + "-forwarded" },
                    ServiceBrokerProvisioning.ForwardDestinationServiceName,
                    options);
            }
        }

        // Builds one harness instance hosting BOTH receivers: the source receiver on the ForwardingSet target
        // queue (handled by the explicitly-registered ForwardingSourceHandler) and the destination receiver on
        // the ForwardDestination queue (handled by RecordingMessageHandler<ForwardedCommand>, wired by passing
        // typeof(ForwardedCommand) as a recorded message type). Both receivers run in default ReceiveOnly mode.
        // The harness is built with ForwardingSet as its owning object set so the default SendAsync routes the
        // SourceCommand to the ForwardingSet source service.
        private ChatterSsbPipelineHarness BuildHarness()
            => ChatterSsbPipelineHarness.Build(
                _fixture.GetAppConnectionString(),
                ServiceBrokerProvisioning.ForwardingSet,
                ssb =>
                {
                    // Source receiver: consumes SourceCommand on the ForwardingSet target queue. Register the
                    // forwarding handler explicitly so Chatter resolves it (not a RecordingMessageHandler) for
                    // SourceCommand on the receive path.
                    ssb.AddQueueReceiver<SourceCommand>(
                        ServiceBrokerProvisioning.ForwardingSet.TargetQueuePathBracketed,
                        deadLetterServicePath: ServiceBrokerProvisioning.ForwardingSet.DeadLetterServiceName);
                    ssb.Services.AddTransient<IMessageHandler<SourceCommand>, ForwardingSourceHandler>();

                    // Destination receiver: a SECOND receiver, on the distinct ForwardDestination queue, that
                    // delivers the forwarded command to its RecordingMessageHandler<ForwardedCommand>. SSB hosts
                    // both receivers in one instance.
                    ssb.AddQueueReceiver<ForwardedCommand>(
                        "[" + ServiceBrokerProvisioning.ForwardDestinationQueueName + "]",
                        deadLetterServicePath: ServiceBrokerProvisioning.ForwardingSet.DeadLetterServiceName);
                },
                typeof(ForwardedCommand));

        // C10 in-handler forward through Chatter: a SourceCommand sent to the ForwardingSet source service is
        // consumed by a handler that forwards a ForwardedCommand to the distinct ForwardDestination service via
        // the broker context's Send. A SECOND in-process Chatter receiver on the ForwardDestination service
        // delivers the forwarded command to its RecordingMessageHandler, proving the forward routed through
        // Chatter's SSB send path with the payload intact — end to end. Asserts the destination handler is
        // invoked once with the forwarded "-forwarded" payload, observed at the Chatter handler / MessageContext
        // level.
        [RequiresDockerFact]
        public async Task HandlerForwardsToDestinationServiceThroughChatterSsbSendPath()
        {
            var harness = BuildHarness();
            try
            {
                await harness.StartAsync();

                // Drive the chain: send a SourceCommand to the ForwardingSet source service (the harness's
                // owning object set, so the default SendAsync targets it).
                await harness.SendAsync(new SourceCommand { Value = "origin" });

                // The source handler's forward (Chatter's SSB send path) must land the ForwardedCommand on the
                // distinct ForwardDestination service, where the second Chatter receiver delivers it to its
                // RecordingMessageHandler.
                var handled = await harness.WaitForHandledAsync<ForwardedCommand>(HandlerWait);

                // Payload round-trip: the forwarded command must arrive with the exact payload the source handler
                // forwarded, deserialized by Chatter on the destination receive path.
                handled.Message.Should().NotBeNull(
                    "the forwarded command must be delivered to the ForwardDestination service through Chatter's SSB send path");
                handled.Message.Value.Should().Be(
                    "origin-forwarded",
                    "the destination receiver's handler must receive the exact payload the source handler forwarded");

                // Inbound broker context: the destination handler must receive an IMessageBrokerContext on the SSB
                // receive path, carrying the InfrastructureType stamp that identifies the SSB receiver.
                handled.Context.Should().NotBeNull(
                    "the destination handler context must be an IMessageBrokerContext on the broker receive path");
                var inbound = handled.Context.BrokeredMessage;

                inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                inbound.MessageContext[MessageContext.InfrastructureType]
                    .Should().Be(SSBMessageContext.InfrastructureType);

                // The forwarded command must reach the destination handler exactly once. The post-dispatch
                // EndDialog system message (EndConversationAfterDispatch default true) on the destination
                // conversation is auto-acked by the receiver's classifier and never reaches the handler.
                harness.GetSignal<ForwardedCommand>().InvocationCount
                    .Should().Be(1,
                        "the forwarded command reaches the destination handler once; the EndDialog system message " +
                        "is auto-acked and must not reach the handler");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
    }
}
