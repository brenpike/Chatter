using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Integration
{
    // CRITERION 3: DB-enforced inbox idempotency over a real SQL Server database with the PRODUCTION model
    // (MessageId primary key). The MessageId PK is what orders two deliveries of the same id: the second
    // delivery's claim waits on the first delivery's uncommitted key lock and then resolves against whatever that
    // first delivery did with it.
    [Trait("Category", "Integration")]
    [Collection(EfReliabilitySqlServerCollection.Name)]
    public class WhenDeduplicatingInboxOnSqlServer
    {
        // SQL Server error number for a primary-key / unique-constraint violation.
        private const int PrimaryKeyViolationNumber = 2627;

        // Observes a lock wait on THIS test's own database (database_id = DB_ID() excludes the sibling test classes
        // sharing the container). A blocked request is a running request in a waiting state, so it is visible here
        // for as long as it waits.
        private const string BlockedLockRequestQuery =
            "SELECT TOP 1 r.session_id, r.blocking_session_id, r.wait_type " +
            "FROM sys.dm_exec_requests AS r " +
            "WHERE r.database_id = DB_ID() AND r.blocking_session_id <> 0 AND r.wait_type LIKE 'LCK%';";

        // Bounds the observation so an insert that never blocks fails finitely instead of hanging the collection.
        private const int MaxBlockedRequestPolls = 100;
        private static readonly TimeSpan BlockedRequestPollInterval = TimeSpan.FromMilliseconds(100);

        // Bounds the wait for the parked handler to be entered, for the same reason.
        private static readonly TimeSpan HandlerRendezvousTimeout = TimeSpan.FromSeconds(30);

        private readonly EfReliabilitySqlServerFixture _fixture;

        public WhenDeduplicatingInboxOnSqlServer(EfReliabilitySqlServerFixture fixture)
            => _fixture = fixture;

        [RequiresDockerFact]
        public async Task MustRejectDuplicateInboxMessageIdAtThePrimaryKeyConstraint()
        {
            var harness = await CreateHarnessAsync();
            var messageId = Guid.NewGuid().ToString();

            using (var firstContext = harness.CreateContext())
            {
                firstContext.Set<InboxMessage>().Add(new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = DateTime.UtcNow });
                await firstContext.SaveChangesAsync();
            }

            using var secondContext = harness.CreateContext();
            secondContext.Set<InboxMessage>().Add(new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = DateTime.UtcNow });

            Func<Task> duplicateInsert = () => secondContext.SaveChangesAsync();

            var thrown = await duplicateInsert.Should().ThrowAsync<DbUpdateException>();
            thrown.WithInnerException<SqlException>()
                .Which.Number.Should().Be(PrimaryKeyViolationNumber,
                    "a duplicate inbox MessageId must violate the primary-key constraint (SQL Server error 2627)");
        }

        // The database, not the application, orders two deliveries of the same MessageId. Under
        // READ_COMMITTED_SNAPSHOT a second delivery's READ cannot see the first transaction's uncommitted marker,
        // so a read can never order them; snapshot isolation applies to reads only and leaves write locks intact,
        // so the second INSERT of the same primary key waits on the first transaction's key lock until it resolves.
        // The wait itself is what is asserted, read out of sys.dm_exec_requests: an elapsed-time or sleep-then-assert
        // check would hold whether or not the database ordered anything.
        [RequiresDockerFact]
        public async Task MustBlockADuplicateInboxInsertUntilTheHoldingTransactionResolves()
        {
            var connectionString = await CreateSnapshotIsolatedDatabaseAsync();
            var harness = SqlServerOutboxContextHarness.Create(connectionString);
            var messageId = Guid.NewGuid().ToString();

            using var firstContext = harness.CreateContext();
            using var secondContext = harness.CreateContext();

            using var holdingTransaction = await firstContext.Database.BeginTransactionAsync();
            firstContext.Set<InboxMessage>().Add(new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = DateTime.UtcNow });
            await firstContext.SaveChangesAsync();

            using var blockedTransaction = await secondContext.Database.BeginTransactionAsync();
            secondContext.Set<InboxMessage>().Add(new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = DateTime.UtcNow });
            var blockedInsert = Task.Run(() => secondContext.SaveChangesAsync());

            var blockedRequest = await WaitForBlockedLockRequestAsync(connectionString);

            // The holding transaction is released and the blocked insert's outcome captured BEFORE any assertion
            // runs: an assertion that exits the method with the insert still in flight races the transaction
            // disposal below and reports that race instead of the assertion that failed.
            await holdingTransaction.CommitAsync();
            var duplicateInsertFailure = await CaptureFailureAsync(blockedInsert);

            blockedRequest.Should().NotBeNull(
                "the duplicate insert must wait on the holding transaction's uncommitted MessageId key lock");

            duplicateInsertFailure.Should().BeOfType<DbUpdateException>(
                    "the released insert must fail rather than add a second row")
                .Which.InnerException.Should().BeOfType<SqlException>()
                .Which.Number.Should().Be(PrimaryKeyViolationNumber,
                    "the released insert must resolve at the MessageId primary-key constraint (SQL Server error 2627)");

            using var verifyContext = harness.CreateContext();
            var rows = await verifyContext.Set<InboxMessage>().Where(m => m.MessageId == messageId).ToListAsync();
            rows.Should().HaveCount(1, "the blocked insert must not add a second row");
        }

        // The same ordering, driven through BrokeredMessageInbox inside a UnitOfWork transaction rather than by a
        // hand-written insert: a second delivery of a MessageId another delivery is still handling must be ordered
        // behind that delivery and must not reach the handler.
        [RequiresDockerFact]
        public async Task MustInvokeTheHandlerOnceWhenASecondDeliveryRacesTheSameMessageId()
        {
            var connectionString = await CreateSnapshotIsolatedDatabaseAsync();
            var harness = SqlServerOutboxContextHarness.Create(connectionString);
            var messageId = Guid.NewGuid().ToString();

            var handlerInvocations = 0;
            var secondHandlerInvocations = 0;
            var firstHandlerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstHandlerReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var firstDelivery = Task.Run(() => DeliverViaInboxAsync(harness, messageId, async () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                firstHandlerEntered.TrySetResult(true);
                await firstHandlerReleased.Task;
            }));

            await firstHandlerEntered.Task.WaitAsync(HandlerRendezvousTimeout);

            var secondDelivery = Task.Run(() => DeliverViaInboxAsync(harness, messageId, () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                Interlocked.Increment(ref secondHandlerInvocations);
                return Task.CompletedTask;
            }));

            var blockedRequest = await WaitForBlockedLockRequestAsync(connectionString);

            firstHandlerReleased.TrySetResult(true);
            var firstDeliveryFailure = await CaptureFailureAsync(firstDelivery);
            var secondDeliveryFailure = await CaptureFailureAsync(secondDelivery);

            blockedRequest.Should().NotBeNull(
                "the second delivery must be ordered behind the first delivery's uncommitted inbox claim by a lock wait");
            handlerInvocations.Should().Be(1,
                "a delivery of a MessageId another delivery has already claimed must not reach the handler");
            // WHICH delivery loses is the assertion, not an inference from the total: the delivery that claimed
            // FIRST keeps its handler's work and the one that arrived second is the one absorbed. A count of one
            // alone would also hold if the winner's work were the work discarded.
            secondHandlerInvocations.Should().Be(0,
                "the delivery that claimed the MessageId second is the one that must be absorbed");
            firstDeliveryFailure.Should().BeNull("the claiming delivery must commit its handler's work with its inbox marker");
            secondDeliveryFailure.Should().BeNull("the losing delivery must be absorbed as a duplicate, not surfaced as an error");

            // The MessageId primary key guarantees a single row whether or not the two deliveries are ordered, so
            // this is a companion to the handler count rather than the assertion that separates the two cases.
            using var verifyContext = harness.CreateContext();
            var rows = await verifyContext.Set<InboxMessage>().Where(m => m.MessageId == messageId).ToListAsync();
            rows.Should().HaveCount(1, "the MessageId primary key admits exactly one inbox row");
        }

        // The mirror of the fact above: when the claiming delivery ROLLS BACK instead of committing, the claim it
        // was holding goes with it, and the delivery that waited behind it must go on to handle the message. An
        // ordering that only ever suppressed the waiter would pass the fact above and lose the message here.
        [RequiresDockerFact]
        public async Task MustInvokeTheHandlerOnTheWaitingDeliveryWhenTheClaimingDeliveryRollsBack()
        {
            var connectionString = await CreateSnapshotIsolatedDatabaseAsync();
            var harness = SqlServerOutboxContextHarness.Create(connectionString);
            var messageId = Guid.NewGuid().ToString();

            var waitingHandlerInvocations = 0;
            var abandonedHandlerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var abandonedHandlerReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var abandonedDelivery = Task.Run(() => DeliverViaInboxAsync(harness, messageId, async () =>
            {
                abandonedHandlerEntered.TrySetResult(true);
                await abandonedHandlerReleased.Task;
                throw new DeliveryAbandonedException();
            }));

            await abandonedHandlerEntered.Task.WaitAsync(HandlerRendezvousTimeout);

            var waitingDelivery = Task.Run(() => DeliverViaInboxAsync(harness, messageId, () =>
            {
                Interlocked.Increment(ref waitingHandlerInvocations);
                return Task.CompletedTask;
            }));

            var blockedRequest = await WaitForBlockedLockRequestAsync(connectionString);

            abandonedHandlerReleased.TrySetResult(true);
            var abandonedDeliveryFailure = await CaptureFailureAsync(abandonedDelivery);
            var waitingDeliveryFailure = await CaptureFailureAsync(waitingDelivery);

            blockedRequest.Should().NotBeNull(
                "the waiting delivery must be ordered behind the abandoned delivery's uncommitted inbox claim by a lock wait");
            abandonedDeliveryFailure.Should().BeOfType<DeliveryAbandonedException>(
                "the abandoned delivery must fail through its handler rather than at the claim");
            waitingDeliveryFailure.Should().BeNull("the waiting delivery must complete once the claim it waited on is rolled back");
            waitingHandlerInvocations.Should().Be(1,
                "a rolled-back claim releases the message id, so the delivery that waited on it must handle the message");

            using var verifyContext = harness.CreateContext();
            var rows = await verifyContext.Set<InboxMessage>().Where(m => m.MessageId == messageId).ToListAsync();
            rows.Should().HaveCount(1, "the waiting delivery's own claim is the only one that commits");
        }

        // The expired-marker branch of the same ordering. An expired marker is refreshed IN PLACE rather than
        // inserted a second time, so both deliveries issue an UPDATE against one existing row and the MessageId
        // primary key separates nothing: it is ReceivedByInboxAtUtc being a concurrency token that carries the value
        // each delivery read into its own UPDATE predicate, so the delivery that reaches the row second matches no
        // row once the first has committed a newer one.
        [RequiresDockerFact]
        public async Task MustInvokeTheHandlerOnceWhenASecondDeliveryRefreshesTheSameExpiredMessageId()
        {
            var connectionString = await CreateSnapshotIsolatedDatabaseAsync();
            var harness = SqlServerOutboxContextHarness.Create(connectionString);
            var messageId = Guid.NewGuid().ToString();
            var staleReceivedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var deduplicationWindow = TimeSpan.FromMinutes(1);
            await GivenACommittedMarkerAsync(harness, messageId, staleReceivedAtUtc);

            var handlerInvocations = 0;
            var secondHandlerInvocations = 0;
            var firstHandlerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstHandlerReleased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var firstDelivery = Task.Run(() => DeliverViaInboxAsync(harness, messageId, async () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                firstHandlerEntered.TrySetResult(true);
                await firstHandlerReleased.Task;
            }, deduplicationWindow));

            await firstHandlerEntered.Task.WaitAsync(HandlerRendezvousTimeout);

            var secondDelivery = Task.Run(() => DeliverViaInboxAsync(harness, messageId, () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                Interlocked.Increment(ref secondHandlerInvocations);
                return Task.CompletedTask;
            }, deduplicationWindow));

            var blockedRequest = await WaitForBlockedLockRequestAsync(connectionString);

            firstHandlerReleased.TrySetResult(true);
            var firstDeliveryFailure = await CaptureFailureAsync(firstDelivery);
            var secondDeliveryFailure = await CaptureFailureAsync(secondDelivery);

            blockedRequest.Should().NotBeNull(
                "the second delivery's refresh must be ordered behind the first delivery's uncommitted one by a lock wait");
            handlerInvocations.Should().Be(1,
                "a delivery of an expired MessageId another delivery has already refreshed must not reach the handler");
            // WHICH delivery loses is the assertion, not an inference from the total: the delivery that refreshed
            // FIRST keeps its handler's work and the one that arrived second is the one absorbed. A count of one
            // alone would also hold if the winner's work were the work discarded.
            secondHandlerInvocations.Should().Be(0,
                "the delivery whose refresh matched no row is the one that must be absorbed");
            firstDeliveryFailure.Should().BeNull("the refreshing delivery must commit its handler's work with its refreshed marker");
            secondDeliveryFailure.Should().BeNull("the losing delivery must be absorbed as a duplicate, not surfaced as an error");

            using var verifyContext = harness.CreateContext();
            var rows = await verifyContext.Set<InboxMessage>().Where(m => m.MessageId == messageId).ToListAsync();
            rows.Should().ContainSingle("the expired marker is refreshed in place, not inserted a second time")
                .Which.ReceivedByInboxAtUtc.Should().BeAfter(staleReceivedAtUtc,
                    "the refresh that committed must be the one the winning delivery wrote");
        }

        private static async Task DeliverViaInboxAsync(SqlServerOutboxContextHarness harness,
                                                       string messageId,
                                                       Func<Task> handler,
                                                       TimeSpan? deduplicationWindow = null)
        {
            using var context = harness.CreateContext();
            var inbox = new BrokeredMessageInbox<SqlServerOutboxContext>(
                context,
                CreateLogger(),
                new ReliabilityOptions(),
                new EntityFrameworkReliabilityOptions { InboxDeduplicationWindow = deduplicationWindow });
            var unitOfWork = new UnitOfWork<SqlServerOutboxContext>(context, NullLogger<UnitOfWork<SqlServerOutboxContext>>.Instance);

            await unitOfWork.ExecuteAsync(_ => inbox.ReceiveViaInbox("payload", CreateBrokerContext(messageId), handler), null);
        }

        private static async Task GivenACommittedMarkerAsync(SqlServerOutboxContextHarness harness, string messageId, DateTime receivedAtUtc)
        {
            using var seedContext = harness.CreateContext();
            seedContext.Set<InboxMessage>().Add(new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = receivedAtUtc });
            await seedContext.SaveChangesAsync();
        }

        // Returns a formatted description of the first blocked lock request seen on this test's database, or null
        // when the poll cap is exhausted without one. The delay between polls is a polling interval, not the oracle:
        // the assertion is on the observed wait, which only exists if the database actually blocked the request.
        private static async Task<string> WaitForBlockedLockRequestAsync(string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            for (var poll = 0; poll < MaxBlockedRequestPolls; poll++)
            {
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = BlockedLockRequestQuery;

                    await using var reader = await command.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        return $"session_id={reader.GetInt16(0)}, blocking_session_id={reader.GetInt16(1)}, wait_type={reader.GetString(2)}";
                    }
                }

                await Task.Delay(BlockedRequestPollInterval);
            }

            return null;
        }

        // Awaits a delivery and returns its failure, or null when it completed. Both deliveries' outcomes are
        // captured before any assertion runs so that a failing assertion on one cannot leave the other's exception
        // unobserved.
        private static async Task<Exception> CaptureFailureAsync(Task delivery)
        {
            try
            {
                await delivery;
                return null;
            }
            catch (Exception deliveryFailure)
            {
                return deliveryFailure;
            }
        }

        // Creates the per-class database with READ_COMMITTED_SNAPSHOT ON. Azure SQL Database enables it by default,
        // and it is the setting under which a read cannot order two deliveries of the same MessageId, so it is the
        // setting these facts must observe.
        private async Task<string> CreateSnapshotIsolatedDatabaseAsync()
        {
            var connectionString = await _fixture.CreateDatabaseAsync("ef_inbox_dedup");
            var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            var masterConnectionString = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;

            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;";
            await command.ExecuteNonQueryAsync();

            return connectionString;
        }

        private async Task<SqlServerOutboxContextHarness> CreateHarnessAsync()
        {
            var connectionString = await _fixture.CreateDatabaseAsync("ef_inbox_dedup");
            return SqlServerOutboxContextHarness.Create(connectionString);
        }

        private static IMessageBrokerContext CreateBrokerContext(string messageId)
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.Setup(c => c.ContentType).Returns("application/json");

            return new MessageBrokerContext(
                messageId,
                Array.Empty<byte>(),
                new Dictionary<string, object>(),
                "test-receiver",
                CancellationToken.None,
                converter.Object);
        }

        private static ILogger<BrokeredMessageInbox<SqlServerOutboxContext>> CreateLogger()
            => new Mock<ILogger<BrokeredMessageInbox<SqlServerOutboxContext>>>().Object;

        // Deliberately its own type rather than an InvalidOperationException: the inbox refuses a claim outside a
        // transaction with an InvalidOperationException, and a fact that abandons a delivery must not be able to
        // mistake that refusal for its own handler's failure.
        private sealed class DeliveryAbandonedException : Exception
        {
            public DeliveryAbandonedException()
                : base("Simulated failure of a handler that had already claimed its message id.")
            { }
        }
    }
}
