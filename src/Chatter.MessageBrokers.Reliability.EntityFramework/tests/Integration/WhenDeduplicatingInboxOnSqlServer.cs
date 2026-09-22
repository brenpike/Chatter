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
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Integration
{
    // CRITERION 3: DB-enforced inbox idempotency over a real SQL Server database with the PRODUCTION model
    // (MessageId primary key), and the ordering that store imposes on two deliveries of one message id. The claim
    // BrokeredMessageInbox flushes before it invokes the handler holds the row's exclusive lock for that handler's
    // whole duration, so a concurrent delivery of the same message id waits on the lock instead of racing past a
    // read. Rationale:
    // docs/adr/0033-the-relational-inbox-claims-before-the-handler-and-stamps-handled-after-it-in-the-same-row.md.
    //
    // Every racing fact below runs against a database with READ_COMMITTED_SNAPSHOT ON. Without it the second
    // delivery's own READ blocks on the claim's share lock, the race self-serialises before either delivery reaches
    // a write, and the facts pass without the store having ordered anything.
    [Trait("Category", "Integration")]
    [Collection(EfReliabilitySqlServerCollection.Name)]
    public class WhenDeduplicatingInboxOnSqlServer
    {
        // SQL Server error number for a primary-key / unique-constraint violation.
        private const int PrimaryKeyViolationNumber = 2627;

        // Finite cap on the blocked-request observation. The cap bounds the wait; the assertions are on what
        // sys.dm_exec_requests reported, never on how long the poll took to report it.
        private const int MaxBlockedRequestPolls = 100;
        private static readonly TimeSpan BlockedRequestPollInterval = TimeSpan.FromMilliseconds(50);

        // Ages the seeded marker far past the window, so expiry is settled by the seeded value rather than by how
        // long the test takes to run.
        private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MarkerAge = TimeSpan.FromHours(1);

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

        // INVARIANT: two deliveries of one message id invoke ONE handler. What this pins is the handler COUNT, not
        // the row count: the MessageId primary key admits one row whether or not the store ordered anything, so a
        // row-count assertion here would pass on an unordered race. Deleting the claim's flush from
        // ClaimMessageIdAsync, so the claim is staged and takes no lock, reddens six facts on both target
        // frameworks, this one among them, at "Expected handlerInvocations to be 1 ... but found 2" - measured by
        // deleting it and counting, not predicted.
        [RequiresDockerFact]
        public async Task MustInvokeTheHandlerOnceWhenTwoDeliveriesRaceForTheSameMessageId()
        {
            var harness = await CreateRacingHarnessAsync();
            var messageId = Guid.NewGuid().ToString();
            var claimingHandlerEntered = CreateSignal();
            var releaseClaimingHandler = CreateSignal();
            var handlerInvocations = 0;

            using var claimingContext = harness.CreateContext();
            using var racingContext = harness.CreateContext();

            var claimingDelivery = DeliverAsync(claimingContext, messageId, async () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                claimingHandlerEntered.SetResult(true);
                await releaseClaimingHandler.Task;
            });

            await AwaitHandlerEntryAsync(claimingHandlerEntered.Task, claimingDelivery);

            var racingDelivery = DeliverAsync(racingContext, messageId, () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                return Task.CompletedTask;
            });

            // The poll is the sequencing point here rather than the claim: it holds the winner's handler open until
            // the racing claim is on the lock. MustMakeASecondDeliverysClaimWaitOnTheFirstsRowLock is the fact that
            // asserts on the wait itself.
            await WaitForBlockedRequestAsync(harness.ConnectionString);
            releaseClaimingHandler.SetResult(true);

            // Both deliveries are settled, and each one's outcome captured, before the first assertion runs. An
            // assertion that exits the test while a blocked flush is still in flight takes the connection's disposal
            // with it, and that disposal failure replaces the assertion message.
            var claimingOutcome = await Record.ExceptionAsync(() => claimingDelivery);
            var racingOutcome = await Record.ExceptionAsync(() => racingDelivery);

            handlerInvocations.Should().Be(1,
                "the claim the winner flushed holds the row for the whole of its handler, so the delivery racing it never reaches a handler");
            claimingOutcome.Should().BeNull("the delivery that claimed the message id first is the one that settles");
            racingOutcome.Should().BeOfType<DbUpdateException>("the racing claim meets the row the winner committed")
                .Which.InnerException.Should().BeOfType<SqlException>()
                .Which.Number.Should().Be(PrimaryKeyViolationNumber,
                    "the racing claim loses specifically at the MessageId primary key (SQL Server error 2627)");
            (await ReadCommittedMarkerAsync(harness, messageId)).ReceivedByInboxAtUtc
                .Should().NotBeNull("the winner stamped its claim handled before the unit of work committed");
        }

        // INVARIANT: the ordering is the STORE's, observed as the wait itself - a request in this database whose
        // blocking_session_id names another session and whose wait_type is a lock wait. Elapsed time is not
        // evidence: a timing assertion reads the same whether the second delivery waited on a lock or simply ran
        // second. The observation has teeth in both directions, measured on both target frameworks: deleting the
        // claim's flush from ClaimMessageIdAsync reddens this fact, and so does giving the racing delivery a
        // DIFFERENT message id, which leaves the two deliveries with nothing to contend for and the poll returning
        // nothing.
        [RequiresDockerFact]
        public async Task MustMakeASecondDeliverysClaimWaitOnTheFirstsRowLock()
        {
            var harness = await CreateRacingHarnessAsync();
            var messageId = Guid.NewGuid().ToString();
            var claimingHandlerEntered = CreateSignal();
            var releaseClaimingHandler = CreateSignal();

            using var claimingContext = harness.CreateContext();
            using var racingContext = harness.CreateContext();

            var claimingDelivery = DeliverAsync(claimingContext, messageId, async () =>
            {
                claimingHandlerEntered.SetResult(true);
                await releaseClaimingHandler.Task;
            });

            await AwaitHandlerEntryAsync(claimingHandlerEntered.Task, claimingDelivery);

            var racingDelivery = DeliverAsync(racingContext, messageId, () => Task.CompletedTask);

            var blockedRequest = await WaitForBlockedRequestAsync(harness.ConnectionString);
            releaseClaimingHandler.SetResult(true);

            var claimingOutcome = await Record.ExceptionAsync(() => claimingDelivery);
            await Record.ExceptionAsync(() => racingDelivery);

            blockedRequest.Should().NotBeNull(
                "the racing delivery's claim must be waiting on the lock the claiming delivery's flush took");
            blockedRequest.Value.BlockingSessionId.Should().NotBe(0);
            blockedRequest.Value.WaitType.Should().StartWith("LCK");
            claimingOutcome.Should().BeNull("the delivery that claimed the message id first is the one that settles");
        }

        // INVARIANT: the claim is only as durable as the transaction carrying it, so a rollback hands the message id
        // back and the delivery waiting on the lock handles the message. Committing the claim inside
        // ClaimMessageIdAsync, which is what would make a claim outlive the handler that failed, reddens fifteen
        // facts on both target frameworks, this one at "Expected waitingOutcome to be <null>"; the breadth is that
        // mutation's, because ending the transaction early changes every step downstream of the claim. Deleting the
        // claim's flush leaves this fact GREEN, measured: with nothing flushed there is no lock to wait on and no
        // claim to roll back, and the second delivery handles the message for a different reason. So the wait's
        // release by a rollback is what this fact carries alone, and the handler count alone does not separate it.
        [RequiresDockerFact]
        public async Task MustInvokeTheWaitingDeliverysHandlerWhenTheClaimingDeliveryRollsBack()
        {
            var harness = await CreateRacingHarnessAsync();
            var messageId = Guid.NewGuid().ToString();
            var claimingHandlerEntered = CreateSignal();
            var releaseClaimingHandler = CreateSignal();
            var claimingFailure = new InvalidOperationException("claiming handler failed");
            var waitingHandlerInvocations = 0;

            using var claimingContext = harness.CreateContext();
            using var waitingContext = harness.CreateContext();

            var claimingDelivery = DeliverAsync(claimingContext, messageId, async () =>
            {
                claimingHandlerEntered.SetResult(true);
                await releaseClaimingHandler.Task;
                throw claimingFailure;
            });

            await AwaitHandlerEntryAsync(claimingHandlerEntered.Task, claimingDelivery);

            var waitingDelivery = DeliverAsync(waitingContext, messageId, () =>
            {
                Interlocked.Increment(ref waitingHandlerInvocations);
                return Task.CompletedTask;
            });

            // Sequencing, not the claim: the waiting delivery must be on the lock before the claiming one is let go.
            await WaitForBlockedRequestAsync(harness.ConnectionString);
            releaseClaimingHandler.SetResult(true);

            var claimingOutcome = await Record.ExceptionAsync(() => claimingDelivery);
            var waitingOutcome = await Record.ExceptionAsync(() => waitingDelivery);

            waitingHandlerInvocations.Should().Be(1,
                "the rolled-back claim leaves the message id unclaimed, so the delivery waiting on it handles the message");
            waitingOutcome.Should().BeNull("the delivery that waited goes on to settle the message id itself");
            claimingOutcome.Should().BeSameAs(claimingFailure);
            (await ReadCommittedMarkerAsync(harness, messageId)).ReceivedByInboxAtUtc
                .Should().NotBeNull("the delivery that did handle the message stamped the marker handled");
        }

        // INVARIANT: a marker the deduplication window has aged out is re-claimed rather than read, so two
        // redeliveries racing it still invoke ONE handler, and the timestamp that survives is the winner's HANDLED
        // stamp. The claim carries no timestamp, which is what the in-handler read establishes: the surviving value
        // was written after the handler was already running, not when the message id was claimed. Deleting the
        // claim's flush from ClaimMessageIdAsync reddens this fact at "Expected handlerInvocations to be 1 ... but
        // found 2", one of the six that mutation reddens on both target frameworks (measured).
        [RequiresDockerFact]
        public async Task MustInvokeTheHandlerOnceWhenTwoRedeliveriesRaceAnExpiredMarker()
        {
            var harness = await CreateRacingHarnessAsync();
            var messageId = Guid.NewGuid().ToString();
            var expiredStamp = DateTime.UtcNow - MarkerAge;
            await SeedCommittedMarkerAsync(harness, messageId, expiredStamp);

            var claimingHandlerEntered = CreateSignal();
            var releaseClaimingHandler = CreateSignal();
            var handlerInvocations = 0;
            InboxMessage markerInsideTheClaimingHandler = null;

            using var claimingContext = harness.CreateContext();
            using var racingContext = harness.CreateContext();

            var claimingDelivery = DeliverAsync(claimingContext, messageId, async () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                markerInsideTheClaimingHandler = await ReadMarkerAsync(claimingContext, messageId);
                claimingHandlerEntered.SetResult(true);
                await releaseClaimingHandler.Task;
            }, DeduplicationWindow);

            await AwaitHandlerEntryAsync(claimingHandlerEntered.Task, claimingDelivery);

            var racingDelivery = DeliverAsync(racingContext, messageId, () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                return Task.CompletedTask;
            }, DeduplicationWindow);

            var blockedRequest = await WaitForBlockedRequestAsync(harness.ConnectionString);
            releaseClaimingHandler.SetResult(true);

            var claimingOutcome = await Record.ExceptionAsync(() => claimingDelivery);
            var racingOutcome = await Record.ExceptionAsync(() => racingDelivery);

            handlerInvocations.Should().Be(1,
                "the re-claim of an expired marker takes the same row lock a fresh claim does");
            claimingOutcome.Should().BeNull("the delivery that re-claimed the expired marker first is the one that settles");
            blockedRequest.Should().NotBeNull("the racing re-claim waits on the lock the winner's re-claim took");
            racingOutcome.Should().BeOfType<DbUpdateConcurrencyException>(
                "the racing re-claim wrote against a timestamp the winner replaced");
            markerInsideTheClaimingHandler.ReceivedByInboxAtUtc
                .Should().BeNull("the re-claim the winner flushed before its handler carries no timestamp");

            var committedMarker = await ReadCommittedMarkerAsync(harness, messageId);
            committedMarker.ReceivedByInboxAtUtc.Should().NotBeNull();
            committedMarker.ReceivedByInboxAtUtc.Should().NotBe(expiredStamp,
                "the surviving timestamp is the one written when the winner's handler returned");
        }

        // INVARIANT: the residue a swallowed handler failure leaves - a committed marker carrying no timestamp - is
        // re-claimed under the same lock a fresh claim takes, so two redeliveries racing it invoke ONE handler. The
        // re-claim writes no timestamp over no timestamp, so the statement that takes the lock is the only thing
        // separating these two deliveries. This fact is the oracle for that forced write: deleting the IsModified
        // line from ClaimMessageIdAsync's existing-marker branch reddens this fact and NO other in the project, on
        // both target frameworks, at "Expected handlerInvocations to be 1 ... but found 2" - measured by deleting
        // it and counting. Deleting the claim's flush reddens it too, as one of that mutation's six.
        [RequiresDockerFact]
        public async Task MustInvokeTheHandlerOnceWhenTwoRedeliveriesRaceAClaimNoHandlerCompleted()
        {
            var harness = await CreateRacingHarnessAsync();
            var messageId = Guid.NewGuid().ToString();
            await SeedCommittedMarkerAsync(harness, messageId, null);

            var claimingHandlerEntered = CreateSignal();
            var releaseClaimingHandler = CreateSignal();
            var handlerInvocations = 0;

            using var claimingContext = harness.CreateContext();
            using var racingContext = harness.CreateContext();

            var claimingDelivery = DeliverAsync(claimingContext, messageId, async () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                claimingHandlerEntered.SetResult(true);
                await releaseClaimingHandler.Task;
            });

            await AwaitHandlerEntryAsync(claimingHandlerEntered.Task, claimingDelivery);

            var racingDelivery = DeliverAsync(racingContext, messageId, () =>
            {
                Interlocked.Increment(ref handlerInvocations);
                return Task.CompletedTask;
            });

            var blockedRequest = await WaitForBlockedRequestAsync(harness.ConnectionString);
            releaseClaimingHandler.SetResult(true);

            var claimingOutcome = await Record.ExceptionAsync(() => claimingDelivery);
            var racingOutcome = await Record.ExceptionAsync(() => racingDelivery);

            handlerInvocations.Should().Be(1,
                "the forced re-claim takes the row's lock even though it writes the value already there");
            claimingOutcome.Should().BeNull("the delivery that re-claimed the message id first is the one that settles");
            blockedRequest.Should().NotBeNull(
                "the re-claim writes no timestamp over no timestamp, and the racing delivery waits on the lock that write takes");
            racingOutcome.Should().BeOfType<DbUpdateConcurrencyException>(
                "the racing re-claim wrote against the absent timestamp the winner replaced");
            (await ReadCommittedMarkerAsync(harness, messageId)).ReceivedByInboxAtUtc
                .Should().NotBeNull("the winner's handler returned, so its marker carries a handled timestamp");
        }

        private async Task<SqlServerOutboxContextHarness> CreateHarnessAsync()
        {
            var connectionString = await _fixture.CreateDatabaseAsync("ef_inbox_dedup");
            return SqlServerOutboxContextHarness.Create(connectionString);
        }

        // READ_COMMITTED_SNAPSHOT is switched on from a master connection before the harness opens any pooled
        // connection to the new database, because the setting cannot be taken while another session holds the
        // database. Azure SQL Database defaults it ON, so ON is the honest setting to race against.
        private async Task<RacingHarness> CreateRacingHarnessAsync()
        {
            var connectionString = await _fixture.CreateDatabaseAsync("ef_inbox_race");
            await EnableReadCommittedSnapshotAsync(connectionString);

            return new RacingHarness(SqlServerOutboxContextHarness.Create(connectionString), connectionString);
        }

        private async Task EnableReadCommittedSnapshotAsync(string connectionString)
        {
            var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

            await using var master = new SqlConnection(_fixture.GetMasterConnectionString());
            await master.OpenAsync();

            await using var command = master.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;";
            await command.ExecuteNonQueryAsync();
        }

        // One delivery, inside its OWN unit of work over its OWN context: the transaction the claim is flushed into
        // is the one this delivery's handler runs in, and it ends when this delivery settles.
        private static Task DeliverAsync(SqlServerOutboxContext context,
                                         string messageId,
                                         Func<Task> handler,
                                         TimeSpan? deduplicationWindow = null)
        {
            var inbox = new BrokeredMessageInbox<SqlServerOutboxContext>(
                context,
                CreateLogger(),
                new ReliabilityOptions(),
                new EntityFrameworkReliabilityOptions { InboxDeduplicationWindow = deduplicationWindow });

            // NullLogger rather than a Moq double: UnitOfWork<TContext> is internal, and Castle DynamicProxy refuses
            // to proxy ILogger<T> when T names a type it cannot see from its own strong-named proxy assembly.
            var unitOfWork = new UnitOfWork<SqlServerOutboxContext>(
                context,
                NullLogger<UnitOfWork<SqlServerOutboxContext>>.Instance);

            return unitOfWork.ExecuteAsync(ct => inbox.ReceiveViaInbox("payload", CreateBrokerContext(messageId, ct), handler), null);
        }

        // RunContinuationsAsynchronously so completing a signal cannot resume the waiter inline on the thread that
        // is still inside a delivery.
        private static TaskCompletionSource<bool> CreateSignal()
            => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Waits for the parked handler to be entered, and surfaces a delivery that failed before ever reaching its
        // handler rather than waiting on a signal that can no longer arrive.
        private static async Task AwaitHandlerEntryAsync(Task handlerEntered, Task delivery)
        {
            var settled = await Task.WhenAny(handlerEntered, delivery);
            if (!ReferenceEquals(settled, delivery))
            {
                return;
            }

            await delivery;
            throw new InvalidOperationException("The claiming delivery completed without entering its handler.");
        }

        // Observes the WAIT ITSELF from sys.dm_exec_requests. The database_id filter keeps a sibling test class
        // sharing the container out of the answer. Returns null when the poll cap is reached with nothing blocked.
        private static async Task<(short BlockingSessionId, string WaitType)?> WaitForBlockedRequestAsync(string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            for (var attempt = 0; attempt < MaxBlockedRequestPolls; attempt++)
            {
                var blockedRequest = await ReadBlockedRequestAsync(connection);
                if (blockedRequest.HasValue)
                {
                    return blockedRequest;
                }

                await Task.Delay(BlockedRequestPollInterval);
            }

            return null;
        }

        private static async Task<(short BlockingSessionId, string WaitType)?> ReadBlockedRequestAsync(SqlConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT TOP 1 blocking_session_id, wait_type FROM sys.dm_exec_requests " +
                "WHERE database_id = DB_ID() AND blocking_session_id <> 0 AND wait_type LIKE 'LCK%';";

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return null;
            }

            return (reader.GetInt16(0), reader.GetString(1));
        }

        // Seeded through its own context and committed, so a delivery that reads this marker reads it from the store
        // the way a redelivery on a fresh scope does.
        private static async Task SeedCommittedMarkerAsync(RacingHarness harness, string messageId, DateTime? receivedAtUtc)
        {
            using var context = harness.CreateContext();
            context.Set<InboxMessage>().Add(new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = receivedAtUtc });
            await context.SaveChangesAsync();
        }

        // AsNoTracking so the answer comes from the row rather than from a change tracker the delivery left behind.
        private static Task<InboxMessage> ReadMarkerAsync(SqlServerOutboxContext context, string messageId)
            => context.Set<InboxMessage>().AsNoTracking().SingleOrDefaultAsync(m => m.MessageId == messageId);

        private static async Task<InboxMessage> ReadCommittedMarkerAsync(RacingHarness harness, string messageId)
        {
            using var context = harness.CreateContext();
            return await ReadMarkerAsync(context, messageId);
        }

        private static IMessageBrokerContext CreateBrokerContext(string messageId, CancellationToken cancellationToken = default)
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.Setup(c => c.ContentType).Returns("application/json");

            return new MessageBrokerContext(
                messageId,
                Array.Empty<byte>(),
                new Dictionary<string, object>(),
                "test-receiver",
                cancellationToken,
                converter.Object);
        }

        private static ILogger<BrokeredMessageInbox<SqlServerOutboxContext>> CreateLogger()
            => new Mock<ILogger<BrokeredMessageInbox<SqlServerOutboxContext>>>().Object;

        // Pairs the harness with the connection string the blocked-request observation needs: that poll runs on its
        // own connection to the same database, outside either delivery's transaction.
        private sealed class RacingHarness
        {
            private readonly SqlServerOutboxContextHarness _contexts;

            public RacingHarness(SqlServerOutboxContextHarness contexts, string connectionString)
            {
                _contexts = contexts;
                ConnectionString = connectionString;
            }

            public string ConnectionString { get; }

            public SqlServerOutboxContext CreateContext() => _contexts.CreateContext();
        }
    }
}
