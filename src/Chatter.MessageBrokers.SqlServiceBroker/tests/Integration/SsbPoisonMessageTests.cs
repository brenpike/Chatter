using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Poison-message→DLQ integration proof (C8) for the SQL Service Broker integration harness. The SYSTEM UNDER
    // TEST is Chatter's POISON path, which is DISTINCT from the throwing-handler deadletter path
    // (SsbDeadLetterTests): when the inbound envelope's INNER body cannot be deserialized into TCommand,
    // BrokeredMessageReceiver.ProcessMessageAsync wraps the failure in a PoisonedMessageException
    // (GetMessageFromBody<TMessage> throws), and the worker's PoisonedMessageException catch deadletters
    // IMMEDIATELY via TryDeadletterWithRecoveryAsync — BEFORE the handler dispatch and WITHOUT consulting
    // MaxReceiveAttempts. So the message reaches the DLQ on the FIRST delivery, the handler is NEVER invoked
    // (InvocationCount == 0), and there is no retry/redelivery climb to bound.
    //
    // HOW THE POISON IS RAW-INJECTED AT THE ADO.NET EDGE: Chatter's own SendAsync would serialize a VALID
    // envelope whose inner body binds cleanly to TCommand, so it can never produce a poison. This test therefore
    // bypasses Chatter's sender and SENDs raw, mirroring SsbDeadLetterTests' DLQ RECEIVE inverted into a SEND:
    //   1. Build a REAL OutboundBrokeredMessage envelope via JsonUnicodeBodyConverter — the exact UTF-16-JSON
    //      wire format SqlServiceBrokerReceiver expects — so the OUTER envelope deserialize in ReceiveMessageAsync
    //      succeeds and message.MessageTypeName == //Chatter/BrokeredMessage (the Chatter-brokered path that
    //      reaches ProcessMessageAsync). The envelope's ContentType header is stamped to the UTF-16 converter's
    //      content type by OutboundBrokeredMessage's ctor, so the receiver decodes the INNER body with that same
    //      converter.
    //   2. Set the envelope's INNER Body to UTF-16 bytes of a NON-JSON string ("this-is-not-valid-json"). The
    //      inner-body decode (JsonSerializer.Deserialize<PoisonCommand>) then throws, surfacing the
    //      PoisonedMessageException the poison path keys on. The inner bytes are deliberately undeserializable to
    //      TCommand rather than merely a different shape, so the poison trigger is deterministic.
    //   3. Serialize that envelope to bytes with the SAME UTF-16 converter and SEND it raw via BEGIN DIALOG /
    //      SEND ON CONVERSATION onto the harness's shared initiator service → the PoisonSet target service →
    //      PoisonSet target queue, on the production-pinned //Chatter contract + //Chatter/BrokeredMessage
    //      message type. This is the inverse of SsbDeadLetterTests' raw DLQ RECEIVE.
    //
    // The fact is gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green. Mirrors SsbDeadLetterTests / SsbNackRedeliveryTests for harness setup and
    // collection membership; uses the dedicated PoisonSet so its queue state is isolated from the other
    // integration test classes in the collection.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbPoisonMessageTests
    {
        private static readonly TimeSpan DeadLetterWait = TimeSpan.FromSeconds(30);

        // After the poison lands on the DLQ, how long to keep watching the handler signal to PROVE no retry climb
        // ever invokes the handler. The poison deadletters before dispatch, so the handler must never run; a
        // bounded settle confirms the negative is not merely un-raced.
        private static readonly TimeSpan NoInvocationSettle = TimeSpan.FromSeconds(3);

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbPoisonMessageTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        // Distinct command type so this test's queue state is independent of the other integration tests in the
        // collection. Its shape is irrelevant to the test: the poison body is deliberately NON-JSON so it can
        // never bind to this (or any) command type.
        public sealed class PoisonCommand : ICommand
        {
            public string Marker { get; set; }
        }

        private ChatterSsbPipelineHarness BuildHarness()
            => ChatterSsbPipelineHarness.Build(
                _fixture.GetAppConnectionString(),
                ServiceBrokerProvisioning.PoisonSet,
                // maxReceiveAttempts is left at the default: the poison path deadletters on the FIRST delivery
                // independent of MaxReceiveAttempts (the PoisonedMessageException catch runs before the
                // delivery-count ladder), so the trigger does not depend on the attempt cap.
                ssb => ssb.AddQueueReceiver<PoisonCommand>(
                    ServiceBrokerProvisioning.PoisonSet.TargetQueuePathBracketed,
                    deadLetterServicePath: ServiceBrokerProvisioning.PoisonSet.DeadLetterServiceName),
                typeof(PoisonCommand));

        // Poison→DLQ: an undeserializable inner body raw-injected at the ADO.NET edge deadletters on the FIRST
        // delivery. Assert (a) the poison lands on the deadletter QUEUE, (b) the deadletter envelope carries the
        // poison failure headers (FailureDetails == "Poisoned message received"), (c) ReceiveAttempts == 1 (no
        // retry climb), and (d) the handler was NEVER invoked (InvocationCount == 0), confirmed across a bounded
        // settle so the negative is not vacuous.
        [RequiresDockerFact]
        public async Task UndeserializableBodyDeadlettersImmediatelyWithoutInvokingHandler()
        {
            var harness = BuildHarness();
            try
            {
                await harness.StartAsync();

                // Raw-inject the poison: a valid //Chatter/BrokeredMessage envelope whose INNER body is non-JSON,
                // sent bypassing Chatter's serializer so the receive-side inner-body decode throws
                // PoisonedMessageException.
                await SendRawPoisonAsync(DeadLetterWait);

                // The poison deadletters AFTER the inner-body decode throws on the receiver thread, so poll the
                // DLQ (bounded) until the deadlettered envelope arrives rather than racing the dispatch.
                var deadLettered = await ReceiveFromDeadLetterQueueAsync(DeadLetterWait);

                deadLettered.Should().NotBeNull(
                    "an undeserializable inner body must surface a PoisonedMessageException whose worker catch " +
                    "deadletters immediately to the configured deadletter service, which delivers onto the DLQ");

                // The deadletter envelope carries the poison failure headers DeadletterMessageAsync stamps.
                // FailureDetails is the deadLetterReason, fixed to "Poisoned message received" on the poison path.
                deadLettered.MessageContext.Should().ContainKey(MessageContext.FailureDetails,
                    "DeadletterMessageAsync stamps FailureDetails with the deadletter reason");
                deadLettered.MessageContext[MessageContext.FailureDetails].ToString()
                    .Should().Be("Poisoned message received",
                        "the poison path passes the fixed 'Poisoned message received' reason to the deadletter");

                deadLettered.MessageContext.Should().ContainKey(MessageContext.FailureDescription,
                    "DeadletterMessageAsync stamps FailureDescription with the originating exception's ToString");
                deadLettered.MessageContext[MessageContext.FailureDescription].ToString()
                    .Should().Contain(nameof(PoisonCommand),
                        "the poison failure description carries the PoisonedMessageException, which names the " +
                        "target type it could not deserialize");

                // InfrastructureType identifies the SSB receiver as the deadletter origin.
                deadLettered.MessageContext.Should().ContainKey(MessageContext.InfrastructureType);
                deadLettered.MessageContext[MessageContext.InfrastructureType].ToString()
                    .Should().Be(SSBMessageContext.InfrastructureType);

                // NO RETRY CLIMB: the poison deadletters on the first delivery, so the receiver's local attempt
                // counter is at 1 — the poison path never nacks/redelivers to climb it.
                deadLettered.MessageContext.Should().ContainKey(MessageContext.ReceiveAttempts);
                Convert.ToInt32(deadLettered.MessageContext[MessageContext.ReceiveAttempts])
                    .Should().Be(1, "the poison deadlettered on the first delivery with no redelivery climb");

                // HANDLER NEVER INVOKED: the poison surfaces during the inner-body decode BEFORE the handler
                // dispatch, so the handler is never reached. Let the pump loop for a bounded settle so a (wrongly)
                // redelivered/dispatched poison would have time to invoke the handler, then assert it stayed at 0
                // — proving the negative is observed, not merely un-raced.
                await Task.Delay(NoInvocationSettle).ConfigureAwait(false);

                harness.GetSignal<PoisonCommand>().InvocationCount
                    .Should().Be(0,
                        "the poison must deadletter before the handler dispatch, so the handler must never be " +
                        "invoked even after a bounded settle window");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // Raw-injects a poison message at the ADO.NET edge: BEGIN DIALOG from the shared initiator service TO the
        // PoisonSet target service on the //Chatter contract, then SEND ON CONVERSATION a valid
        // //Chatter/BrokeredMessage envelope whose INNER body is non-JSON. Bypasses Chatter's sender entirely so
        // the body is undeserializable to TCommand (Chatter's own SendAsync could only ever produce a clean
        // envelope). Bounds every DB await to the supplied timeout so a wedged BEGIN DIALOG / SEND fails fast.
        private async Task SendRawPoisonAsync(TimeSpan timeout)
        {
            var bodyConverter = new JsonUnicodeBodyConverter();

            // The INNER body is UTF-16 bytes of a non-JSON string. OutboundBrokeredMessage's ctor stamps the
            // envelope ContentType to the UTF-16 converter's content type, so the receiver decodes this inner
            // body with the SAME UTF-16 converter and JsonSerializer.Deserialize<PoisonCommand> throws on it.
            var poisonInnerBody = bodyConverter.GetBytes("this-is-not-valid-json");

            // A real OutboundBrokeredMessage envelope (valid OUTER shape so the receiver's envelope deserialize
            // succeeds and routes through the Chatter-brokered path) carrying the poison inner body. A destination
            // is required by the ctor but is irrelevant to the raw SEND below, which targets the PoisonSet service
            // directly; use the target service name for clarity.
            var envelope = new OutboundBrokeredMessage(
                Guid.NewGuid().ToString(),
                poisonInnerBody,
                new Dictionary<string, object>(),
                ServiceBrokerProvisioning.PoisonSet.TargetServiceName,
                bodyConverter);

            var envelopeBytes = bodyConverter.Convert(envelope);

            using var operationCts = new CancellationTokenSource(timeout);
            var operationToken = operationCts.Token;

            await using var connection = new SqlConnection(_fixture.GetAppConnectionString());
            await connection.OpenAsync(operationToken).ConfigureAwait(false);

            // BEGIN DIALOG FROM the shared initiator service TO the PoisonSet target service on the //Chatter
            // contract — the same dialog Chatter's sender would open, but here we drive it raw so we control the
            // body. BeginDialogConversationCommand strips brackets from the target and uses it as TO SERVICE.
            var beginDialog = new BeginDialogConversationCommand(
                connection,
                targetServiceName: ServiceBrokerProvisioning.PoisonSet.TargetServiceName,
                initiatorServiceName: ServiceBrokerProvisioning.InitiatorServiceName,
                serviceContractName: ServiceBrokerProvisioning.ContractName);
            var conversationHandle = await beginDialog.ExecuteAsync(operationToken).ConfigureAwait(false);

            // SEND the poison envelope on the //Chatter/BrokeredMessage message type so the receiver classifies it
            // as the Chatter-brokered path and reaches ProcessMessageAsync (where the inner-body decode throws).
            var sendOnConversation = new SendOnConversationCommand(
                connection,
                conversationHandle,
                envelopeBytes,
                messageType: ServiceBrokerProvisioning.MessageTypeName);
            await sendOnConversation.ExecuteAsync(operationToken).ConfigureAwait(false);
        }

        // Bounded poll of the deadletter queue, reading the deadlettered OutboundBrokeredMessage envelope at the
        // test edge via raw ADO.NET. Mirrors SsbDeadLetterTests' DLQ RECEIVE: RECEIVE is non-blocking so an empty
        // queue returns immediately and the poll loop sleeps before retrying; returns null if the deadline
        // elapses so the caller's assertion fails fast rather than hanging. The decompress CASE mirrors
        // production's RECEIVE so the message_body bytes are the raw envelope regardless of broker-side
        // compression.
        private async Task<OutboundBrokeredMessage> ReceiveFromDeadLetterQueueAsync(TimeSpan timeout)
        {
            var bodyConverter = new JsonUnicodeBodyConverter();
            var deadline = DateTime.UtcNow + timeout;

            using var operationCts = new CancellationTokenSource(timeout);
            var operationToken = operationCts.Token;

            await using var connection = new SqlConnection(_fixture.GetAppConnectionString());
            await connection.OpenAsync(operationToken).ConfigureAwait(false);

            while (DateTime.UtcNow < deadline)
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(operationToken).ConfigureAwait(false);

                byte[] messageBody = null;
                await using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "RECEIVE TOP(1) " +
                        "CASE WHEN SUBSTRING(message_body, 1, 2) = 0x1F8B " +
                        "THEN CAST(decompress(message_body) AS VARBINARY(MAX)) " +
                        "ELSE message_body END AS message_body, message_type_name " +
                        $"FROM [{ServiceBrokerProvisioning.PoisonSet.DeadLetterQueueName}]";

                    await using var reader = await command.ExecuteReaderAsync(operationToken).ConfigureAwait(false);
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
