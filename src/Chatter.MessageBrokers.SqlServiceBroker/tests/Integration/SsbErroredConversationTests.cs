using System;
using System.Linq;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Errored-conversation proofs for SqlServiceBrokerReceiver (#357).
    //
    // When one side of a dialog issues END CONVERSATION ... WITH ERROR, Service Broker delivers an
    // http://schemas.microsoft.com/SQL/ServiceBroker/Error system message to the OTHER side's queue and parks
    // that endpoint in the 'ER' state. An errored conversation can carry no further message, so the only
    // correct receive outcome is: never dispatch the Error message as a Chatter envelope, log the decoded
    // error payload for the operator, and END CONVERSATION so the endpoint leaves 'ER'. Leaving it in 'ER'
    // leaks a conversation endpoint per fault, forever.
    //
    // The same rule is the broader #357 decision: EVERY terminal receive outcome that settles a real message
    // ends that message's conversation. NullBodyDiscardEndsItsConversation pins the null-body arm of that rule
    // at the live-SQL edge; ServiceBrokerMessageClassifier.EndsConversation is its unit oracle.
    //
    // The receiver is constructed DIRECTLY (internal, via InternalsVisibleTo) rather than through
    // ChatterSsbPipelineHarness, mirroring SsbMissingQueueTests: these facts are about the receive seam
    // itself, which the pump would otherwise fold into a background loop. A RecordingLoggerCreator is
    // injected so the Error-payload log is observable — it is also the non-vacuity guard, proving the RECEIVE
    // actually consumed the Error message rather than timing out on an empty queue.
    //
    // Gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green. Uses the dedicated ErrorSet so the faulted endpoints and any Error-message
    // residue stay isolated from the other integration test classes in the collection.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbErroredConversationTests
    {
        // Finite WAITFOR timeout so an empty queue never wedges an iteration of the receive drive.
        private const int ReceiverTimeoutInMilliseconds = 2000;

        // The error code and description carried by the fault. The description round-trips through the Error
        // message body, so it is what ServiceBrokerErrorPayload.Describe must surface into the operator log.
        private const int ErrorCode = 1;
        private const string ErrorDescription = "integration-error";

        // Bounds the receive drive so a wedged connection cannot hang the collection.
        private static readonly TimeSpan ReceiveBound = TimeSpan.FromSeconds(30);

        // Bounds every raw ADO.NET arrange/assert step.
        private static readonly TimeSpan OperationBound = TimeSpan.FromSeconds(30);

        // Bounds the catalog-view polls. Same-database delivery settles in milliseconds; this is the race guard.
        private static readonly TimeSpan CatalogPollBound = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan CatalogPollInterval = TimeSpan.FromMilliseconds(250);

        // Conversation-endpoint states (sys.conversation_endpoints.state) this suite reasons about.
        private const string ErrorState = "ER";
        private const string ClosedState = "CD";
        private const string ConversingState = "CO";

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbErroredConversationTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        private static string QueuePath => ServiceBrokerProvisioning.ErrorSet.TargetQueuePathBracketed;

        // An Error system message means the conversation has FAULTED: it can carry no further message and the
        // target endpoint is parked in 'ER' until the application ends it. Assert that the receiver (a) never
        // dispatches the Error message, (b) logs the decoded error payload at Error so the operator sees WHY
        // the conversation faulted, and (c) ends the conversation, so the endpoint leaves 'ER'.
        //
        // (c) is asserted as "absent OR 'CD'", never as absence alone: END CONVERSATION moves the endpoint to
        // the CLOSED state, which Service Broker retains for roughly 30 minutes by design, and
        // CleanupOnEndConversation defaults to false — so the row is expected to still be there.
        [RequiresDockerFact]
        public async Task AnErroredConversationIsEndedRatherThanLeftInTheErrorState()
        {
            var (conversationId, targetHandle) = await FaultAConversationAsync();

            // Non-vacuity guard for the ARRANGE: the fault must actually have reached the target endpoint,
            // otherwise a receiver that dispatched nothing would look correct for the wrong reason.
            var faultedState = await PollTargetEndpointStateAsync(conversationId, state => state == ErrorState);
            faultedState.Should().Be(ErrorState,
                "END CONVERSATION WITH ERROR on the initiator must deliver an Error message to the target and " +
                "park the target endpoint in the error state before the receiver is driven");

            var logger = new RecordingLoggerCreator<SqlServiceBrokerReceiver>(newContext: null);
            var receiver = CreateReceiver(logger);
            MessageBrokerContext dispatched;
            try
            {
                await receiver.InitializeAsync(
                    new ReceiverOptions { MessageReceiverPath = QueuePath },
                    CancellationToken.None);

                dispatched = await DriveReceiveUntilAsync(receiver, logger, IsErrorLog, ReceiveBound);
            }
            finally
            {
                await receiver.DisposeAsync();
            }

            // (a) An Error message is not a Chatter envelope. Dispatching it would hand the core pipeline a
            // faulted conversation's system payload as if it were an inbound message.
            dispatched.Should().BeNull(
                "an Error system message reports a FAULTED conversation, so it must be discarded rather than " +
                "dispatched through the Chatter envelope path");

            // (b) The operator log names the conversation and carries the decoded payload. This is also the
            // proof that the RECEIVE consumed the Error message rather than timing out on an empty queue.
            var errorLogs = logger.LoggedMessages.Where(IsErrorLog).ToList();
            errorLogs.Should().ContainSingle(
                "a faulted conversation is an operator-visible fault, so the discard must log exactly once at " +
                "Error rather than at the Trace level the routine discards use");
            errorLogs[0].message.Should().Contain(targetHandle.ToString(),
                "the operator must be told WHICH conversation faulted");
            errorLogs[0].message.Should().Contain(ErrorDescription,
                "the Error message body carries the originator's DESCRIPTION, and ServiceBrokerErrorPayload " +
                "must decode it into the log instead of leaving the operator with an opaque discard");

            // (c) The endpoint left 'ER'. A retained CLOSED row is the expected steady state.
            var settledState = await PollTargetEndpointStateAsync(conversationId, IsEnded);
            IsEnded(settledState).Should().BeTrue(
                $"the discard must END CONVERSATION so the endpoint leaves '{ErrorState}'; a closed endpoint is " +
                $"retained for roughly 30 minutes by design and CleanupOnEndConversation defaults to false, so " +
                $"the row is expected to remain as '{ClosedState}' rather than disappear (observed " +
                $"'{settledState ?? "<absent>"}')");
        }

        // The discard must COMMIT the RECEIVE, not roll it back: a rolled-back RECEIVE returns the Error
        // message to the queue and the receiver spins on it forever. Scoped to this test's own conversation so
        // residue from another test in the collection cannot green (or redden) it.
        [RequiresDockerFact]
        public async Task AnErroredConversationsMessageIsRemovedFromTheQueue()
        {
            var (conversationId, targetHandle) = await FaultAConversationAsync();

            var faultedState = await PollTargetEndpointStateAsync(conversationId, state => state == ErrorState);
            faultedState.Should().Be(ErrorState,
                "the fault must have reached the target endpoint before the receiver is driven");

            var logger = new RecordingLoggerCreator<SqlServiceBrokerReceiver>(newContext: null);
            var receiver = CreateReceiver(logger);
            try
            {
                await receiver.InitializeAsync(
                    new ReceiverOptions { MessageReceiverPath = QueuePath },
                    CancellationToken.None);

                await DriveReceiveUntilAsync(receiver, logger, IsErrorLog, ReceiveBound);
            }
            finally
            {
                await receiver.DisposeAsync();
            }

            var remaining = await PeekMessageTypeForConversationAsync(targetHandle);
            remaining.Should().BeNull(
                "the discard must commit the RECEIVE, so no message for the faulted conversation may remain on " +
                $"the queue (observed '{remaining ?? "<none>"}')");
        }

        // The broader #357 decision at the live-SQL edge: a terminal discard that settles a REAL message ends
        // that message's conversation, not only the errored-conversation arm. A body-less SEND on an accepted
        // message type yields a NULL message_body, which classifies DiscardNullBody.
        [RequiresDockerFact]
        public async Task ANullBodyDiscardEndsItsConversation()
        {
            var conversationId = await SendBodylessMessageAsync();

            var deliveredState = await PollTargetEndpointStateAsync(conversationId, state => state == ConversingState);
            deliveredState.Should().Be(ConversingState,
                "the body-less message must have reached the target endpoint before the receiver is driven");

            var logger = new RecordingLoggerCreator<SqlServiceBrokerReceiver>(newContext: null);
            var receiver = CreateReceiver(logger);
            MessageBrokerContext dispatched;
            try
            {
                await receiver.InitializeAsync(
                    new ReceiverOptions { MessageReceiverPath = QueuePath },
                    CancellationToken.None);

                dispatched = await DriveReceiveUntilAsync(receiver, logger, IsNullBodyDiscardLog, ReceiveBound);
            }
            finally
            {
                await receiver.DisposeAsync();
            }

            dispatched.Should().BeNull("a message with a null body has nothing to dispatch");
            logger.LoggedMessages.Should().Contain(logged => IsNullBodyDiscardLog(logged),
                "the null-body discard must be reached, otherwise the conversation assertion below is vacuous");

            var settledState = await PollTargetEndpointStateAsync(conversationId, IsEnded);
            IsEnded(settledState).Should().BeTrue(
                "every terminal outcome that settles a received message ends that message's conversation, so a " +
                $"null-body discard must not leave the endpoint conversing (observed '{settledState ?? "<absent>"}')");
        }

        private SqlServiceBrokerReceiver CreateReceiver(RecordingLoggerCreator<SqlServiceBrokerReceiver> logger)
        {
            var ssbOptions = new SqlServiceBrokerOptions(
                connectionString: _fixture.GetAppConnectionString(),
                messageBodyType: "application/json; charset=utf-16",
                receiverTimeoutInMilliseconds: ReceiverTimeoutInMilliseconds);

            // A REAL body converter (not a bare Mock.Of default) so that, before the fix, the Error message
            // takes the dispatch path to completion and the red is a dispatched context rather than an
            // incidental null-converter throw.
            var bodyConverterFactory = new Mock<IBodyConverterFactory>();
            bodyConverterFactory
                .Setup(f => f.CreateBodyConverter(It.IsAny<string>()))
                .Returns(new JsonUnicodeBodyConverter());

            return new SqlServiceBrokerReceiver(
                ssbOptions,
                new SqlClientConnectionSource(ssbOptions),
                new MessageBrokerOptions { TransactionMode = TransactionMode.ReceiveOnly },
                logger.Creation,
                bodyConverterFactory.Object,
                Mock.Of<IServiceScopeFactory>());
        }

        private static bool IsErrorLog((LogLevel level, string message) logged)
            => logged.level == LogLevel.Error;

        private static bool IsNullBodyDiscardLog((LogLevel level, string message) logged)
            => logged.message != null && logged.message.Contains("null message body");

        // A conversation has been ended once its endpoint row is gone or has reached the CLOSED state.
        private static bool IsEnded(string state)
            => state == null || state == ClosedState;

        // Drives ReceiveMessageAsync until the receiver either hands back a context (which the caller asserts
        // against) or records a log the caller recognises as the settle it was waiting for. Each iteration gets
        // its own TransactionContext because the dispatch path publishes the receive connection into it.
        private static async Task<MessageBrokerContext> DriveReceiveUntilAsync(
            SqlServiceBrokerReceiver receiver,
            RecordingLoggerCreator<SqlServiceBrokerReceiver> logger,
            Func<(LogLevel level, string message), bool> settled,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            using var receiveCts = new CancellationTokenSource(timeout);

            while (DateTime.UtcNow < deadline)
            {
                var transactionContext = new TransactionContext(QueuePath, TransactionMode.ReceiveOnly);
                var dispatched = await receiver.ReceiveMessageAsync(transactionContext, receiveCts.Token)
                    .ConfigureAwait(false);

                if (dispatched != null)
                {
                    // The dispatch path hands the caller the still-open RECEIVE's connection and transaction;
                    // the core pipeline would settle them later. Nothing settles them here, so release them
                    // rather than leaking a connection per iteration.
                    DisposeContained(transactionContext);
                    return dispatched;
                }

                if (logger.LoggedMessages.Any(settled))
                {
                    return null;
                }
            }

            return null;
        }

        private static void DisposeContained(TransactionContext transactionContext)
        {
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);
            transaction?.Dispose();
            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            connection?.Dispose();
        }

        // Arranges a FAULTED conversation and returns its conversation_id (shared by both endpoints) together
        // with the TARGET endpoint's conversation_handle.
        //
        // A target endpoint only exists once a message has been delivered to it, and END CONVERSATION WITH
        // ERROR against a target that was never reached delivers nothing. So the dialog is established with a
        // real SEND, that establishing message is drained AT THE TEST EDGE (so the receiver under test meets
        // the Error message first, and so the establishing message's own settlement cannot end the
        // conversation), and only then is the initiator ended with an error. The drain is scoped to THIS
        // conversation so residue from another test in the collection can never be drained in its place.
        private async Task<(Guid ConversationId, Guid TargetHandle)> FaultAConversationAsync()
        {
            using var operationCts = new CancellationTokenSource(OperationBound);
            var operationToken = operationCts.Token;

            await using var connection = new SqlConnection(_fixture.GetAppConnectionString());
            await connection.OpenAsync(operationToken).ConfigureAwait(false);

            var conversationHandle = await BeginDialogAsync(connection, operationToken).ConfigureAwait(false);

            var send = new SendOnConversationCommand(
                connection,
                conversationHandle,
                new byte[] { 0x00 },
                messageType: ServiceBrokerProvisioning.MessageTypeName);
            await send.ExecuteAsync(operationToken).ConfigureAwait(false);

            var conversationId = await ReadConversationIdAsync(connection, conversationHandle, operationToken)
                .ConfigureAwait(false);
            var targetHandle = await PollTargetEndpointHandleAsync(connection, conversationId, operationToken)
                .ConfigureAwait(false);

            await DrainConversationMessageAsync(connection, targetHandle, operationToken).ConfigureAwait(false);

            await using var endWithError = connection.CreateCommand();
            endWithError.CommandText =
                "END CONVERSATION @conversationHandle WITH ERROR = @errorCode DESCRIPTION = @errorDescription;";
            endWithError.Parameters.Add(new SqlParameter("@conversationHandle", conversationHandle));
            endWithError.Parameters.Add(new SqlParameter("@errorCode", ErrorCode));
            endWithError.Parameters.Add(new SqlParameter("@errorDescription", ErrorDescription));
            await endWithError.ExecuteNonQueryAsync(operationToken).ConfigureAwait(false);

            return (conversationId, targetHandle);
        }

        // Arranges a conversation carrying one body-less message on an ACCEPTED message type, and returns its
        // conversation_id. A SEND with no message body yields a NULL message_body, which the receive query
        // reads back as a null Body — the only way DiscardNullBody is reachable over the wire.
        private async Task<Guid> SendBodylessMessageAsync()
        {
            using var operationCts = new CancellationTokenSource(OperationBound);
            var operationToken = operationCts.Token;

            await using var connection = new SqlConnection(_fixture.GetAppConnectionString());
            await connection.OpenAsync(operationToken).ConfigureAwait(false);

            var conversationHandle = await BeginDialogAsync(connection, operationToken).ConfigureAwait(false);

            await using var send = connection.CreateCommand();
            send.CommandText = "SEND ON CONVERSATION @conversationHandle MESSAGE TYPE @messageType;";
            send.Parameters.Add(new SqlParameter("@conversationHandle", conversationHandle));
            send.Parameters.Add(new SqlParameter("@messageType", ServiceBrokerProvisioning.MessageTypeName));
            await send.ExecuteNonQueryAsync(operationToken).ConfigureAwait(false);

            return await ReadConversationIdAsync(connection, conversationHandle, operationToken)
                .ConfigureAwait(false);
        }

        private static async Task<Guid> BeginDialogAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            var beginDialog = new BeginDialogConversationCommand(
                connection,
                targetServiceName: ServiceBrokerProvisioning.ErrorSet.TargetServiceName,
                initiatorServiceName: ServiceBrokerProvisioning.InitiatorServiceName,
                serviceContractName: ServiceBrokerProvisioning.ContractName);
            return await beginDialog.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }

        // Consumes (and autocommits) one message of ONE conversation from the target queue at the test edge.
        private static async Task DrainConversationMessageAsync(SqlConnection connection, Guid conversationHandle, CancellationToken cancellationToken)
        {
            await using var drain = connection.CreateCommand();
            drain.CommandTimeout = 0;
            drain.CommandText =
                $"WAITFOR (RECEIVE TOP(1) message_type_name FROM {QueuePath} " +
                "WHERE conversation_handle = @conversationHandle), TIMEOUT @timeoutInMilliseconds;";
            drain.Parameters.Add(new SqlParameter("@conversationHandle", conversationHandle));
            drain.Parameters.Add(new SqlParameter("@timeoutInMilliseconds", ReceiverTimeoutInMilliseconds));
            await drain.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Non-destructively PEEKS the target queue for messages still queued for ONE conversation, returning
        // the message type of the first, or null when nothing is queued. A service queue is SELECTable, which
        // a RECEIVE scoped to the handle is not once the conversation has been ended — SQL Server rejects that
        // RECEIVE with "The conversation handle ... is not found", so the peek must not go through RECEIVE.
        private async Task<string> PeekMessageTypeForConversationAsync(Guid conversationHandle)
        {
            using var operationCts = new CancellationTokenSource(OperationBound);
            var operationToken = operationCts.Token;

            await using var connection = new SqlConnection(_fixture.GetAppConnectionString());
            await connection.OpenAsync(operationToken).ConfigureAwait(false);

            await using var peek = connection.CreateCommand();
            peek.CommandText =
                $"SELECT TOP(1) message_type_name FROM {QueuePath} " +
                "WHERE conversation_handle = @conversationHandle;";
            peek.Parameters.Add(new SqlParameter("@conversationHandle", conversationHandle));

            var result = await peek.ExecuteScalarAsync(operationToken).ConfigureAwait(false);
            return result == null || result == DBNull.Value ? null : (string)result;
        }

        private static async Task<Guid> ReadConversationIdAsync(SqlConnection connection, Guid conversationHandle, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT conversation_id FROM sys.conversation_endpoints WHERE conversation_handle = @conversationHandle;";
            command.Parameters.Add(new SqlParameter("@conversationHandle", conversationHandle));

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result == null || result == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"No conversation endpoint was found for handle '{conversationHandle}'.");
            }

            return (Guid)result;
        }

        // Bounded poll for the TARGET endpoint's conversation_handle (is_initiator = 0). The target endpoint is
        // materialised by delivery, so it can lag the initiator's SEND by a moment.
        private static async Task<Guid> PollTargetEndpointHandleAsync(SqlConnection connection, Guid conversationId, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + CatalogPollBound;
            while (true)
            {
                var handle = await ReadTargetEndpointScalarAsync(connection, conversationId, "conversation_handle", cancellationToken)
                    .ConfigureAwait(false);
                if (handle != null)
                {
                    return (Guid)handle;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        $"No target conversation endpoint materialised for conversation '{conversationId}'.");
                }

                await Task.Delay(CatalogPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        // Bounded poll for the TARGET endpoint's state, returning as soon as it satisfies the caller's
        // predicate and otherwise returning the LAST observed state (null when the row is absent) so a failing
        // assertion can report what was actually seen.
        private async Task<string> PollTargetEndpointStateAsync(Guid conversationId, Func<string, bool> isSettled)
        {
            using var operationCts = new CancellationTokenSource(OperationBound);
            var operationToken = operationCts.Token;

            await using var connection = new SqlConnection(_fixture.GetAppConnectionString());
            await connection.OpenAsync(operationToken).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + CatalogPollBound;
            string state;
            while (true)
            {
                state = (string)await ReadTargetEndpointScalarAsync(connection, conversationId, "state", operationToken)
                    .ConfigureAwait(false);
                if (isSettled(state) || DateTime.UtcNow >= deadline)
                {
                    return state;
                }

                await Task.Delay(CatalogPollInterval, operationToken).ConfigureAwait(false);
            }
        }

        // Reads one column of the TARGET endpoint row (is_initiator = 0) for a conversation, or null when the
        // row is absent. The column name is a caller-supplied LITERAL from this file, never external input.
        private static async Task<object> ReadTargetEndpointScalarAsync(SqlConnection connection, Guid conversationId, string columnName, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {columnName} FROM sys.conversation_endpoints " +
                "WHERE conversation_id = @conversationId AND is_initiator = 0;";
            command.Parameters.Add(new SqlParameter("@conversationId", conversationId));

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result == DBNull.Value ? null : result;
        }
    }
}
