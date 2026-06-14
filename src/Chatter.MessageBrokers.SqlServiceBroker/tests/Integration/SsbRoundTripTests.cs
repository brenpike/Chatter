using System;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Canonical end-to-end round-trip proof for the SQL Service Broker integration harness (STEP-005). The
    // SYSTEM UNDER TEST is Chatter's own SSB send + receive path: a command dispatched ONLY through Chatter's
    // IBrokeredMessageDispatcher.Send (harness.SendAsync) is delivered by the in-process receiver pump
    // (ChatterSsbPipelineHarness) to a Chatter-resolved RecordingMessageHandler<TMessage>, and every assertion
    // reads the payload Chatter deserialized and the IMessageBrokerContext Chatter built — never a raw SSB /
    // ADO.NET type. This exercises the OutboundBrokeredMessage envelope round-trip through SQL Service Broker
    // and the inbound header stamps SqlServiceBrokerReceiver applies on receive (InfrastructureType,
    // ReceiveAttempts, ServiceName, ServiceContractName, MessageTypeName).
    //
    // The fact is gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the nightly SQL Server CI lane (`--filter Category=Integration`) runs it for
    // real. Mirrors the Azure Service Bus PipelineRoundTripTests analogue.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbRoundTripTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbRoundTripTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        // A command carrying string/int/bool fields so the body round-trip exercises Chatter's STJ body
        // converter for each scalar kind (the on-receive materialization must restore the exact CLR values).
        public sealed class RoundTripCommand : ICommand
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public bool Flag { get; set; }
        }

        private ChatterSsbPipelineHarness BuildHarness()
            => ChatterSsbPipelineHarness.Build(
                _fixture.GetAppConnectionString(),
                ServiceBrokerProvisioning.RoundTripSet,
                ssb => ssb.AddQueueReceiver<RoundTripCommand>(
                    ServiceBrokerProvisioning.RoundTripSet.TargetQueuePathBracketed,
                    deadLetterServicePath: ServiceBrokerProvisioning.RoundTripSet.DeadLetterServiceName),
                typeof(RoundTripCommand));

        // Round-trip: a command sent through Chatter's dispatcher is delivered by Chatter's SSB pump to the
        // registered handler with its string/int/bool payload deserialized EXACTLY, and the inbound
        // SSBMessageContext stamps the receiver applies are present and correct. Asserts the handler was
        // invoked exactly once for the command — the post-dispatch EndDialog system message (the
        // EndConversationAfterDispatch default) is auto-acked by the receiver's classifier and must NOT reach
        // the handler.
        [RequiresDockerFact]
        public async Task SentCommandRoundTripsToHandlerWithPayloadAndBrokerContextStamps()
        {
            var harness = BuildHarness();
            try
            {
                await harness.StartAsync();

                var sent = new RoundTripCommand { Name = "ssb-round-trip", Count = 42, Flag = true };
                await harness.SendAsync(sent);

                var handled = await harness.WaitForHandledAsync<RoundTripCommand>(HandlerWait);

                // Payload round-trip: every scalar kind must survive the OutboundBrokeredMessage envelope
                // round-trip through SQL Service Broker verbatim.
                handled.Message.Should().NotBeNull("Chatter's SSB pipeline must deliver the command to the handler");
                handled.Message.Name.Should().Be("ssb-round-trip");
                handled.Message.Count.Should().Be(42);
                handled.Message.Flag.Should().BeTrue();

                // Inbound broker context: the handler must receive an IMessageBrokerContext on the SSB receive
                // path, carrying the header stamps SqlServiceBrokerReceiver applies in ReceiveMessageAsync.
                handled.Context.Should().NotBeNull(
                    "the handler context must be an IMessageBrokerContext on the broker receive path");
                var inbound = handled.Context.BrokeredMessage;

                // InfrastructureType stamp identifies the SSB receiver.
                inbound.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                inbound.MessageContext[MessageContext.InfrastructureType]
                    .Should().Be(SSBMessageContext.InfrastructureType);

                // ReceiveAttempts is the receiver's local per-conversation delivery count: at least one for a
                // delivered message.
                inbound.MessageContext.Should().ContainKey(MessageContext.ReceiveAttempts);
                Convert.ToInt32(inbound.MessageContext[MessageContext.ReceiveAttempts])
                    .Should().BeGreaterThanOrEqualTo(1, "a delivered message has been received at least once");

                // SSB-specific stamps sourced from the received message: the message arrived on the provisioned
                // target service, over the //Chatter contract, as the //Chatter/BrokeredMessage message type.
                inbound.MessageContext.Should().ContainKey(SSBMessageContext.ServiceName);
                inbound.MessageContext[SSBMessageContext.ServiceName]
                    .Should().Be(ServiceBrokerProvisioning.RoundTripSet.TargetServiceName);

                inbound.MessageContext.Should().ContainKey(SSBMessageContext.ServiceContractName);
                inbound.MessageContext[SSBMessageContext.ServiceContractName]
                    .Should().Be(ServiceBrokerProvisioning.ContractName);

                inbound.MessageContext.Should().ContainKey(SSBMessageContext.MessageTypeName);
                inbound.MessageContext[SSBMessageContext.MessageTypeName]
                    .Should().Be(ServiceBrokerProvisioning.MessageTypeName);

                // The post-dispatch EndDialog system message (EndConversationAfterDispatch default true) is
                // auto-acked by the receiver's classifier and never reaches the handler, so the handler is
                // invoked exactly once — for the command alone.
                harness.GetSignal<RoundTripCommand>().InvocationCount
                    .Should().Be(1, "the EndDialog system message is auto-acked and must not reach the handler");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
    }
}
