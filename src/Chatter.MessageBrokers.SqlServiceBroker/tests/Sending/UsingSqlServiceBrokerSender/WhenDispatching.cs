using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Sending.UsingSqlServiceBrokerSender
{
    // Characterization tests pinning SqlServiceBrokerSender.Dispatch's NEW-connection branch through the
    // real OutboundTransactionPolicy + InMemorySqlConnectionSource, WITHOUT a live Service Broker.
    //
    // REALISM CONSTRAINT (from the deepening plan, verified empirically against System.Data.SqlClient):
    //   - CreateCommand() works on an unopened SqlConnection, so the Scripts command builders' Create()
    //     surface is pinned at the Scripts level (UsingBeginDialogConversationCommand, etc.).
    //   - BeginTransactionAsync() THROWS InvalidOperationException ("the connection is closed") on an
    //     unopened SqlConnection. In Dispatch's new-connection branch this fires AFTER the connection
    //     source is consulted but BEFORE the Begin/Send/End dispatch loop. Therefore, through Dispatch,
    //     only the connection-origin DECISION + fail-fast-and-dispose + propagate contract is unit-
    //     reachable; the Begin/Send/End sequencing, the EndConversationAfterDispatch End-Dialog firing,
    //     the ChatterBrokeredMessageType body-conversion branch, the commit-on-success path, and the
    //     ReuseContext branch all sit behind a live ExecuteNonQueryAsync / a real SqlTransaction (which
    //     cannot be instantiated without a live connection) and are DEFERRED to the end-to-end suite.
    //     See the class-tail NOTE for the full reachable-vs-deferred ledger.
    public class WhenDispatching : Testing.Core.Context
    {
        private static SqlServiceBrokerOptions Options(bool endConversationAfterDispatch = true)
            => new SqlServiceBrokerOptions(
                connectionString: "Server=(local);Database=test;Integrated Security=true;",
                messageBodyType: "application/json; charset=utf-16",
                endConversationAfterDispatch: endConversationAfterDispatch);

        private static OutboundBrokeredMessage Message(
            string destination = "TargetSvc",
            byte[] body = null,
            IDictionary<string, object> messageContext = null)
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            return new OutboundBrokeredMessage(
                Guid.NewGuid().ToString(),
                body ?? new byte[] { 1, 2, 3 },
                messageContext ?? new Dictionary<string, object>(),
                destination,
                bodyConverter.Object);
        }

        private static SqlServiceBrokerSender CreateSender(
            ISqlConnectionSource connectionSource,
            SqlServiceBrokerOptions options = null,
            IBodyConverterFactory bodyConverterFactory = null)
            => new SqlServiceBrokerSender(
                options ?? Options(),
                Mock.Of<ILogger<SqlServiceBrokerSender>>(),
                bodyConverterFactory ?? Mock.Of<IBodyConverterFactory>(),
                connectionSource);

        [Fact]
        public void MustThrowWhenOptionsNull()
        {
            Action act = () => new SqlServiceBrokerSender(
                null,
                Mock.Of<ILogger<SqlServiceBrokerSender>>(),
                Mock.Of<IBodyConverterFactory>(),
                new InMemorySqlConnectionSource());
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenLoggerNull()
        {
            Action act = () => new SqlServiceBrokerSender(
                Options(),
                null,
                Mock.Of<IBodyConverterFactory>(),
                new InMemorySqlConnectionSource());
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenBodyConverterFactoryNull()
        {
            Action act = () => new SqlServiceBrokerSender(
                Options(),
                Mock.Of<ILogger<SqlServiceBrokerSender>>(),
                null,
                new InMemorySqlConnectionSource());
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenConnectionSourceNull()
        {
            Action act = () => new SqlServiceBrokerSender(
                Options(),
                Mock.Of<ILogger<SqlServiceBrokerSender>>(),
                Mock.Of<IBodyConverterFactory>(),
                null);
            act.Should().Throw<ArgumentNullException>();
        }

        // --- New-connection branch: policy decision routes through the connection source ----------------
        //
        // No context transaction (and any TransactionMode) -> OutboundTransactionPolicy resolves
        // NewConnection -> Dispatch consults ISqlConnectionSource.OpenAsync exactly once with
        // CancellationToken.None. This pins that the new-connection branch originates its connection
        // through the port (and not the context transaction). The dispatch then fails fast (see the
        // next tests) before any Begin/Send/End command, so the source-consultation is the deepest
        // assertion reachable on this path without a live server.

        [Fact]
        public async Task MustConsultConnectionSourceOnceOnNewConnectionBranch()
        {
            var connectionSource = new InMemorySqlConnectionSource();
            var sender = CreateSender(connectionSource);

            try
            {
                await sender.Dispatch(Message(), transactionContext: null);
            }
            catch (InvalidOperationException)
            {
                // Expected: BeginTransactionAsync throws on the unopened connection (see class NOTE).
            }

            connectionSource.OpenCount.Should().Be(1,
                "the new-connection branch must originate its connection through ISqlConnectionSource exactly once");
        }

        [Fact]
        public async Task MustConsultConnectionSourceWithNoneCancellationTokenOnNewConnectionBranch()
        {
            var connectionSource = new InMemorySqlConnectionSource();
            var sender = CreateSender(connectionSource);

            try
            {
                await sender.Dispatch(Message(), transactionContext: null);
            }
            catch (InvalidOperationException)
            {
            }

            connectionSource.LastCancellationToken.Should().Be(CancellationToken.None,
                "the sender originates the new connection with CancellationToken.None");
        }

        [Theory]
        [InlineData(TransactionMode.None)]
        [InlineData(TransactionMode.ReceiveOnly)]
        [InlineData(TransactionMode.FullAtomicityViaInfrastructure)]
        public async Task MustTakeNewConnectionBranchWhenNoContextTransactionPresent(TransactionMode transactionMode)
        {
            // With no SqlTransaction in the context container, OutboundTransactionPolicy yields
            // NewConnection for EVERY mode (only FullAtomicityViaInfrastructure + a present context
            // transaction reuses). Pin that all three modes route through the source.
            var connectionSource = new InMemorySqlConnectionSource();
            var sender = CreateSender(connectionSource);
            var transactionContext = new TransactionContext("test-receiver", transactionMode);

            try
            {
                await sender.Dispatch(Message(), transactionContext);
            }
            catch (InvalidOperationException)
            {
            }

            connectionSource.OpenCount.Should().Be(1,
                "without a context transaction the policy resolves NewConnection regardless of TransactionMode");
        }

        [Fact]
        public async Task MustPropagateFailureFromUnopenedConnectionOnNewConnectionBranch()
        {
            // The new-connection branch obtains the (unopened) connection from the source, then calls
            // BeginTransactionAsync on it, which throws InvalidOperationException. Pin that the sender
            // PROPAGATES this failure (does not swallow it) — the !useContextTransaction catch logs and
            // rethrows.
            var connectionSource = new InMemorySqlConnectionSource();
            var sender = CreateSender(connectionSource);

            Func<Task> act = () => sender.Dispatch(Message(), transactionContext: null);

            await act.Should().ThrowAsync<InvalidOperationException>(
                "Dispatch must propagate the failure raised while owning its own transaction lifecycle");
        }

        // --- Collection overload entered directly: open-count == 1 ---------------------------------

        [Fact]
        public async Task MustConsultConnectionSourceOnceWhenCollectionOverloadCalledDirectly()
        {
            // Enter through the collection overload with two messages. BeginTransactionAsync fires
            // after the source is consulted (on the shared, unopened connection) and fails fast.
            // Open-count must be 1: one connection originated through the port regardless of message count.
            var connectionSource = new InMemorySqlConnectionSource();
            var sender = CreateSender(connectionSource);

            try
            {
                await sender.Dispatch(new[] { Message("A"), Message("B") }, transactionContext: null);
            }
            catch (InvalidOperationException)
            {
                // Expected: BeginTransactionAsync throws on the unopened connection (see class NOTE).
            }

            connectionSource.OpenCount.Should().Be(1,
                "the collection overload originates a single connection through ISqlConnectionSource before fail-fast");
        }

        // --- Single→collection delegation: single overload wraps to one-element array (lines 138-139) --

        [Fact]
        public async Task MustConsultConnectionSourceOnceWhenSingleOverloadDelegates()
        {
            // SqlServiceBrokerSender.Dispatch(OutboundBrokeredMessage, TransactionContext) (lines 138-139)
            // wraps the message in a one-element array and delegates to the collection overload.
            // The source must still be consulted exactly once — the wrap-to-one-element-array delegation
            // must not cause a second origination.
            var connectionSource = new InMemorySqlConnectionSource();
            var sender = CreateSender(connectionSource);

            try
            {
                await sender.Dispatch(Message(), transactionContext: null);
            }
            catch (InvalidOperationException)
            {
            }

            connectionSource.OpenCount.Should().Be(1,
                "single-message overload delegates via wrap-to-one-element-array; source must be consulted exactly once");
        }

        // --- Null transactionContext defaults to NewConnection ----------------------------------------

        [Fact]
        public async Task MustRouteToNewConnectionWhenTransactionContextIsNull()
        {
            // null transactionContext defaults to TransactionMode.None (lines 38-40 of SqlServiceBrokerSender.cs).
            // OutboundTransactionPolicy resolves NewConnection when there is no context transaction, so the
            // source must be consulted (open-count == 1) and fail-fast must propagate.
            // INVARIANT: null context never reuses a context transaction.
            var connectionSource = new InMemorySqlConnectionSource();
            var sender = CreateSender(connectionSource);

            try
            {
                await sender.Dispatch(Message(), transactionContext: null);
            }
            catch (InvalidOperationException)
            {
            }

            connectionSource.OpenCount.Should().Be(1,
                "null transactionContext defaults to TransactionMode.None and routes to NewConnection");
        }

        // --- EndConversationAfterDispatch = false origin path ----------------------------------------

        [Fact]
        public async Task MustConsultConnectionSourceAndPropagateFailFastWhenEndConversationAfterDispatchIsFalse()
        {
            // EndConversationAfterDispatch = false suppresses the End-Dialog call in the loop body
            // (SqlServiceBrokerSender.cs line 82), but that effect is DEFERRED behind a live
            // ExecuteNonQueryAsync. The connection-origin path and fail-fast contract remain unit-reachable
            // regardless of this option.
            var connectionSource = new InMemorySqlConnectionSource();
            var options = Options(endConversationAfterDispatch: false);
            var sender = CreateSender(connectionSource, options);

            Func<Task> act = () => sender.Dispatch(Message(), transactionContext: null);

            await act.Should().ThrowAsync<InvalidOperationException>(
                "fail-fast on the unopened connection must propagate regardless of EndConversationAfterDispatch");

            connectionSource.OpenCount.Should().Be(1,
                "source must be consulted once on the new-connection path even when EndConversationAfterDispatch is false");
        }

        // ----------------------------------------------------------------------------------------------
        // REACHABLE-vs-DEFERRED LEDGER (so reviewers know exactly what unit scope pins here):
        //
        // UNIT-REACHABLE (pinned above, through SqlServiceBrokerSender.Dispatch):
        //   * Constructor null guards (options, logger, body-converter factory, connection source).
        //   * New-connection branch consults ISqlConnectionSource exactly once with CancellationToken.None.
        //   * Policy/origin decision: no context transaction => NewConnection for None / ReceiveOnly /
        //     FullAtomicityViaInfrastructure (the source is consulted in all three).
        //   * Fail-fast + propagate: BeginTransactionAsync on the unopened owned connection throws and the
        //     sender rethrows (does not swallow).
        //   * Collection overload entered directly (multiple messages): source consulted once, fail-fast.
        //   * Single→collection delegation (lines 138-139): single-message overload wraps to one-element
        //     array; source still consulted exactly once.
        //   * Null transactionContext defaults to TransactionMode.None (lines 38-40), routes NewConnection,
        //     never reuses a context transaction — source consulted once.
        //   * EndConversationAfterDispatch = false origin path: source consulted once + fail-fast propagates
        //     regardless of the option (the End-Dialog loop-body effect stays DEFERRED, see below).
        //
        // DEFERRED to the end-to-end suite (require a live Service Broker / a real SqlTransaction, which
        // cannot be manufactured without a live connection):
        //   * Ownership/cleanup (connection.Dispose in finally on the !useContextTransaction path):
        //     disposing a NEVER-OPENED SqlConnection leaves NO observable trace (verified empirically —
        //     post-dispose State stays Closed and ConnectionString remains settable without throwing), so
        //     there is no unit-reachable assertion that proves Dispose ran on the fail-fast path. The
        //     dispose contract is reachable only once the connection is actually opened (live SQL).
        //   * Begin -> Send -> End Dialog command SEQUENCING on a successful dispatch (each step runs
        //     ExecuteNonQueryAsync). The SQL SHAPE of each command is already pinned at the Scripts level
        //     (UsingBeginDialogConversationCommand / UsingSendOnConversationCommand /
        //     UsingEndDialogConversationCommand .Create()).
        //   * EndConversationAfterDispatch gate firing an actual End Dialog (loop-body, post-Begin):
        //     the End-Dialog call at line 82 is only reached after BeginConversation's live
        //     ExecuteNonQueryAsync succeeds; unreachable at unit scope.
        //   * ChatterBrokeredMessageType body-conversion branch in SendMessageOnConversation (private,
        //     reached only after BeginConversation's live ExecuteNonQueryAsync succeeds).
        //   * Commit-on-success (transaction.Commit) on the owned path.
        //   * ReuseContext branch (FullAtomicityViaInfrastructure + present context SqlTransaction): a
        //     SqlTransaction has no public constructor and cannot be begun without an open connection, so
        //     it is not constructible at unit scope; its NewConnection-vs-ReuseContext DECISION is pinned
        //     directly on the pure policy (UsingOutboundTransactionPolicy.WhenResolving).
        // ----------------------------------------------------------------------------------------------
    }
}
