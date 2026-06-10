using System;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Deadletter→DLQ integration proof for the SQL Service Broker integration harness (STEP-007). The SYSTEM
    // UNDER TEST is Chatter's deadletter path: when RecordingMessageHandler<T> throws and the receiver's
    // ReceiveAttempts has reached MaxReceiveAttempts, BrokeredMessageReceiver routes to
    // SqlServiceBrokerReceiver.DeadletterMessageAsync, which ends the original dialog and dispatches a NEW
    // OutboundBrokeredMessage to the configured deadletter service (ReceiverOptions.DeadLetterQueuePath) with the
    // failure headers stamped on its MessageContext. The test reads the deadletter QUEUE directly at the test
    // edge and asserts the envelope's failure headers.
    //
    // DEADLETTER TRIGGER LEVER: AddQueueReceiver's maxReceiveAttempts is set to 1. SqlServiceBrokerReceiver does
    // NOT override MessageDeliveryCountAsync, so BrokeredMessageReceiver uses the interface default, which returns
    // the inbound MessageContext.ReceiveAttempts (the receiver's local per-conversation delivery count, stamped
    // starting at 1 on the very first delivery). On the first handler throw the worker computes
    // deliveryCount (1) >= MaxReceiveAttempts (1) and deadletters IMMEDIATELY rather than nacking/redelivering —
    // so the message reaches the DLQ on the first attempt with no redelivery loop to bound.
    //
    // FAILURE-HEADER KEYS ASSERTED (the keys DeadletterMessageAsync stamps onto the deadletter envelope's
    // MessageContext): MessageContext.FailureDescription, MessageContext.FailureDetails,
    // MessageContext.InfrastructureType, MessageContext.ReceiveAttempts.
    //
    // HOW THE DLQ IS READ AT THE TEST EDGE: a raw ADO.NET RECEIVE from the deadletter queue
    // (ServiceBrokerProvisioning.DeadLetterQueueName), mirroring production's decompress CASE so the
    // message_body bytes are the raw envelope, then JsonUnicodeBodyConverter.Convert<OutboundBrokeredMessage>
    // deserializes the envelope EXACTLY as the SqlServiceBrokerSender wrote it (deadletter message type is
    // //Chatter/BrokeredMessage, so the body is the serialized OutboundBrokeredMessage envelope). The deadletter
    // dialog targets the deadletter SERVICE, which delivers onto the deadletter QUEUE, so reading the queue
    // observes the deadlettered message.
    //
    // The fact is gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green. Mirrors SsbNackRedeliveryTests / SsbRoundTripTests for harness setup and
    // collection membership.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbDeadLetterTests
    {
        private static readonly TimeSpan DeadLetterWait = TimeSpan.FromSeconds(30);

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbDeadLetterTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        // Distinct command type so this test's queue state is independent of the other integration tests in the
        // collection.
        public sealed class DeadLetterCommand : ICommand
        {
            public string Marker { get; set; }
        }

        private ChatterSsbPipelineHarness BuildHarness()
            => ChatterSsbPipelineHarness.Build(
                _fixture.GetAppConnectionString(),
                // maxReceiveAttempts: 1 is the deadletter trigger lever — the first throw deadletters immediately
                // because ReceiveAttempts (1) >= MaxReceiveAttempts (1).
                ssb => ssb.AddQueueReceiver<DeadLetterCommand>(
                    ServiceBrokerProvisioning.TargetQueuePathBracketed,
                    deadLetterServicePath: ServiceBrokerProvisioning.DeadLetterServiceName,
                    maxReceiveAttempts: 1),
                typeof(DeadLetterCommand));

        // Deadletter→DLQ: a throwing handler at MaxReceiveAttempts=1 causes DeadletterMessageAsync to send the
        // failed message to the deadletter service. Assert (a) the message lands on the deadletter QUEUE, and
        // (b) the deadletter envelope's MessageContext carries the failure headers the receiver stamps
        // (FailureDescription, FailureDetails, InfrastructureType, ReceiveAttempts).
        [RequiresDockerFact]
        public async Task ThrowingHandlerAtMaxAttemptsDeadlettersToDeadLetterQueue()
        {
            var harness = BuildHarness();
            try
            {
                await harness.StartAsync();

                // Arm the throw BEFORE sending so the handler throws on the first (and only) delivery; with
                // MaxReceiveAttempts=1 that single throw deadletters rather than redelivering, so the throw is
                // left armed for the lifetime of the scenario (no anti-infinite-loop disarm needed — there is no
                // redelivery loop).
                harness.GetSignal<DeadLetterCommand>().ThrowOnHandle =
                    () => new InvalidOperationException("deadletter-test forced throw");

                await harness.SendAsync(new DeadLetterCommand { Marker = "deadletter" });

                // The handler must be invoked at least once before deadlettering can occur. WaitForHandledAsync
                // throws TimeoutException if the handler is never reached, failing fast instead of hanging.
                await harness.WaitForHandledAsync<DeadLetterCommand>(DeadLetterWait);

                // Read the deadletter QUEUE at the test edge. The deadletter dispatch happens AFTER the handler
                // throw on the receiver thread, so poll the queue (bounded) until the deadlettered envelope
                // arrives rather than racing the dispatch.
                var deadLettered = await ReceiveFromDeadLetterQueueAsync(DeadLetterWait);

                deadLettered.Should().NotBeNull(
                    "the throwing handler at MaxReceiveAttempts=1 must cause DeadletterMessageAsync to dispatch " +
                    "the failed message to the deadletter service, which delivers it onto the deadletter queue");

                // The deadletter envelope's MessageContext carries the failure headers DeadletterMessageAsync
                // stamps. FailureDescription is the deadLetterErrorDescription (the handler exception's ToString),
                // FailureDetails is the deadLetterReason ("Poisoned message received").
                deadLettered.MessageContext.Should().ContainKey(MessageContext.FailureDescription,
                    "DeadletterMessageAsync stamps FailureDescription with the deadletter error description");
                deadLettered.MessageContext[MessageContext.FailureDescription].ToString()
                    .Should().Contain("deadletter-test forced throw",
                        "the deadletter error description carries the originating handler exception");

                deadLettered.MessageContext.Should().ContainKey(MessageContext.FailureDetails,
                    "DeadletterMessageAsync stamps FailureDetails with the deadletter reason");

                // InfrastructureType identifies the SSB receiver as the deadletter origin.
                deadLettered.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                deadLettered.MessageContext[MessageContext.InfrastructureType].ToString()
                    .Should().Be(SSBMessageContext.InfrastructureType);

                // ReceiveAttempts must equal the MaxReceiveAttempts that tripped the deadletter (1).
                deadLettered.MessageContext.Should().ContainKey(MessageContext.ReceiveAttempts);
                Convert.ToInt32(deadLettered.MessageContext[MessageContext.ReceiveAttempts])
                    .Should().Be(1, "the deadletter fired on the first delivery (MaxReceiveAttempts=1)");
            }
            finally
            {
                // Disarm before teardown so any in-flight redelivery (defensive; none expected at
                // MaxReceiveAttempts=1) does not throw during the drain.
                harness.GetSignal<DeadLetterCommand>().ThrowOnHandle = null;
                await harness.DisposeAsync();
            }
        }

        // Bounded poll of the deadletter queue, reading the deadlettered OutboundBrokeredMessage envelope at the
        // test edge via raw ADO.NET. RECEIVE is non-blocking (no WAITFOR) so an empty queue returns immediately
        // and the poll loop sleeps before retrying; returns null if the deadline elapses with no message so the
        // caller's assertion fails fast rather than hanging. The decompress CASE mirrors production's RECEIVE so
        // the message_body bytes are the raw envelope regardless of broker-side compression.
        private async Task<OutboundBrokeredMessage> ReceiveFromDeadLetterQueueAsync(TimeSpan timeout)
        {
            var bodyConverter = new JsonUnicodeBodyConverter();
            var deadline = DateTime.UtcNow + timeout;

            await using var connection = new SqlConnection(_fixture.GetAppConnectionString());
            await connection.OpenAsync().ConfigureAwait(false);

            while (DateTime.UtcNow < deadline)
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);

                byte[] messageBody = null;
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "RECEIVE TOP(1) " +
                        "CASE WHEN SUBSTRING(message_body, 1, 2) = 0x1F8B " +
                        "THEN CAST(decompress(message_body) AS VARBINARY(MAX)) " +
                        "ELSE message_body END AS message_body, message_type_name " +
                        $"FROM [{ServiceBrokerProvisioning.DeadLetterQueueName}]";

                    await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                    if (reader.Read() && !reader.IsDBNull(0))
                    {
                        messageBody = reader.GetSqlBytes(0).Buffer;
                    }
                }

                await transaction.CommitAsync().ConfigureAwait(false);

                if (messageBody != null)
                {
                    // The deadletter message type is //Chatter/BrokeredMessage, so the body is the serialized
                    // OutboundBrokeredMessage envelope the SqlServiceBrokerSender wrote.
                    return bodyConverter.Convert<OutboundBrokeredMessage>(messageBody);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }

            return null;
        }
    }
}
