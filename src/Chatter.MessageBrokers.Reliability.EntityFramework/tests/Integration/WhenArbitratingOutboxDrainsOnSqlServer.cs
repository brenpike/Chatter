using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Integration
{
    // CRITERION: the DRAIN CLAIM arbitrates two drains that polled one row, and the relational transaction is what
    // ENFORCES that arbitration. The claim is a different thing from the claim the processed stamp carries
    // (ADR-0031 Option D): that one records that a message WAS dispatched, this one only decides which of several
    // drains gets to try. Every fact here runs the REAL OutboxProcessor over the REAL BrokeredMessageOutbox against
    // the PRODUCTION model on a real SQL Server database, with the messaging infrastructure as the only test double,
    // so what is measured is what a host running this package gets.
    //
    // EVIDENCE IS THE SERVER'S OWN VIEW, NEVER A STOPWATCH. Each fact that claims a drain waited reads
    // sys.dm_exec_requests through SqlServerBlockedRequestProbe and asserts on the session that waited, the session
    // it waited on, the lock wait type and the request status. A slow machine and a real block are indistinguishable
    // from elapsed time, so a timing assertion would pass on a race that never happened.
    //
    // WHY EACH FACT RUNS TWICE: READ_COMMITTED_SNAPSHOT changes what a READ sees, and the arbitration is decided by
    // an UPDATE's U lock, which row versioning does not release. Running every fact with the setting ON and OFF is
    // what makes that a measured claim rather than an inference - and it covers both deployments, since SQL Server
    // defaults it OFF while Azure SQL Database defaults it ON.
    [Trait("Category", "Integration")]
    [Collection(EfReliabilitySqlServerCollection.Name)]
    public class WhenArbitratingOutboxDrainsOnSqlServer : Testing.Core.Context
    {
        private const string Infrastructure = "test-infrastructure";
        private const string Destination = "destination-the-broker-accepts";
        private const string MessageBody = "payload";
        private const string JsonContentType = "application/json";

        // Bounds the wait for the drain claiming a DIFFERENT row, so a drain that is wrongly held by the raced row's
        // lock fails the fact instead of hanging the run. It bounds the FAILURE only: what the fact asserts is the
        // server's own count of what that drain's own session was waiting on.
        private static readonly TimeSpan DifferentRowClaimBudget = TimeSpan.FromSeconds(60);

        private readonly EfReliabilitySqlServerFixture _fixture;

        public WhenArbitratingOutboxDrainsOnSqlServer(EfReliabilitySqlServerFixture fixture)
            => _fixture = fixture;

        // INVARIANT: the drain claim is taken INSIDE the unit of work that publishes, so a second drain that polled
        // the same row waits on that row's lock for the winner's whole publish. The wait is read from the server:
        // the challenger's request is suspended on a lock whose holder is the winning drain's own session, named by
        // session id rather than merely non-zero.
        // Oracle for the placement: this fact, which reddens at "blockedRequest not to be <null>" when the claim
        // stops being held. Hoisting the claim OUT of the unit of work, so it commits on its own, reddens EVERY case
        // in this class - eight, four facts times the two settings, on both target frameworks - because with nothing
        // held there is no wait for any of them to observe; this fact is the one whose red names the wait. It is
        // the mutation that proves the INSIDE-THE-TRANSACTION placement is load-bearing rather than incidental.
        // Racing a DIFFERENT row instead of the raced one reddens THIS fact's two cases and nothing else in the
        // 250-fact project. Both measured by making the change and counting, not predicted - the second prediction
        // held, the first was predicted to redden two cases and reddened eight.
        [RequiresDockerTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MustMakeASecondDrainsClaimWaitOnTheWinningDrainsRowLock(bool readCommittedSnapshot)
        {
            var harness = await CreateArbitrationHarnessAsync(readCommittedSnapshot);

            using var winner = harness.CreateDrain(parksOnDispatch: true);
            using var challenger = harness.CreateDrain(parksOnDispatch: false);

            var winnerMessage = await winner.PollAsync(harness.RacedMessageId);
            var challengerMessage = await challenger.PollAsync(harness.RacedMessageId);

            var winningDrain = winner.Processor.Process(winnerMessage);
            await AwaitDispatchEntryAsync(winner.Dispatcher, winningDrain);
            var winnerSessionId = await SqlServerBlockedRequestProbe.ReadSessionIdAsync(winner.Context);

            var challengingDrain = challenger.Processor.Process(challengerMessage);
            var blockedRequest = await SqlServerBlockedRequestProbe.WaitForRequestBlockedByAsync(harness.ConnectionString, winnerSessionId);

            // Both drains are settled before the first assertion runs. An assertion that exits while a blocked claim
            // is still in flight takes that context's disposal with it, and the disposal failure replaces the
            // assertion message.
            winner.Dispatcher.Release();
            await winningDrain;
            await challengingDrain;

            blockedRequest.Should().NotBeNull(
                "the challenging drain's claim must be waiting on the lock the winning drain's claim took");
            blockedRequest.Value.BlockingSessionId.Should().Be(winnerSessionId,
                "the session holding the lock is the winning drain's own, not merely some other session");
            blockedRequest.Value.SessionId.Should().NotBe(winnerSessionId,
                "the session waiting is the challenging drain's, so no session is reported as blocking itself");
            blockedRequest.Value.WaitType.Should().StartWith("LCK",
                "the wait is on a lock rather than on the network or on a latch");
            blockedRequest.Value.Status.Should().Be("suspended",
                "a request waiting on a lock is suspended, not running");
        }

        // INVARIANT: once the winning drain COMMITS, the claim the waiting drain was blocked on matches nothing - the
        // row now carries the winner's claimed instant and its processed stamp, and neither satisfies the
        // compare-and-set. The denied drain publishes nothing, stamps nothing and spends no dispatch attempt, so the
        // row costs it nothing.
        // Oracle: this fact. Dropping the ProcessedFromOutboxAtUtc and observed-value conjuncts from the claim
        // predicate, which leaves it matching the row by Id alone, reddens THIS fact's two cases and no other fact
        // in this class, at "challenger.Dispatcher.DispatchedMessageIds to be empty ... but found at least one
        // item" - the waiting drain is granted the row the winner already published. That mutation also reddens the
        // five WhenClaimingForDispatch unit facts that pin the same predicate, seven over the 250-fact project
        // (measured by making the change and counting).
        // Hoisting the claim OUT of the unit of work reddens this fact too, but at its VACUITY GUARD and not at its
        // claim: a denial that never had to wait is still a denial. What this fact carries alone is the DENIAL; the
        // wait it guards on belongs to MustMakeASecondDrainsClaimWaitOnTheWinningDrainsRowLock.
        // INVARIANT: a drain claim that COMMITTED carries the processed stamp written with it. The claim and the
        // stamp land in ONE unit of work, so a row showing the winner's claimed instant shows its stamp too and no
        // poll can read the row claimed but unstamped and publish it a second time. The stored-row assertion below
        // reads BOTH columns as a pair, which is what states that rather than the stamp alone.
        // Oracle: this fact. Making the stamp CONDITIONAL inside the unit of work - skipping UpdateProcessedDate
        // unless the row already carries a dispatch attempt - reddens it at
        //   Expected (racedMessage.NextAttemptAtUtc, racedMessage.ProcessedFromOutboxAtUtc.HasValue) to be equal to
        //   { Item1 = <instant>, Item2 = True } ... but found { Item1 = <the SAME instant>, Item2 = False }
        // - the claim is on the row and the stamp is not, which is the state this pin forbids. MEASURED, that
        // mutation reddens TEN facts on EACH target framework, counted over both suites: this fact's two cases;
        // MustGrantTheWaitingDrainsClaimOnceTheWinningDrainRollsBack's two, at their own stamp assertion;
        // UsingBrokeredMessageOutbox.WhenReclaimingAfterAFailedClaimOverSqlite.MustLeaveTheRowProcessedAndSpendNoAttemptWhenTheClaimFailsAfterAPublish;
        // Integration.WhenDrainingPastAPermanentlyFailingRowOnSqlServer.MustDispatchTheMessageBehindAFullBatchOfPermanentlyFailingMessages;
        // and four Chatter.MessageBrokers facts in WhenProcessingOutboxMessage - MustMarkOutboxMessageProcessed,
        // MustMarkProcessedWhenContextContainsNonStringValues, MustReClaimTheRowWhenTheClaimCommitFailsAfterAPublish
        // and MustRecordADispatchAttemptWhenTheReClaimAlsoFails. It is NOT exclusive: every fact that reads a stamped
        // row sees it. What this one holds is the PAIR read off a row whose drain claim COMMITTED over a real server.
        // The nearest sibling, WhenReclaimingAfterAFailedClaimOverSqlite.MustLeaveTheRowProcessedAndSpendNoAttemptWhenTheClaimFailsAfterAPublish,
        // reads the same two columns on the RE-CLAIM exit over SQLite, where the drain's own unit of work rolled back.
        [RequiresDockerTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MustDenyTheWaitingDrainsClaimOnceTheWinningDrainCommits(bool readCommittedSnapshot)
        {
            var harness = await CreateArbitrationHarnessAsync(readCommittedSnapshot);

            using var winner = harness.CreateDrain(parksOnDispatch: true);
            using var challenger = harness.CreateDrain(parksOnDispatch: false);

            var winnerMessage = await winner.PollAsync(harness.RacedMessageId);
            var challengerMessage = await challenger.PollAsync(harness.RacedMessageId);

            var winningDrain = winner.Processor.Process(winnerMessage);
            await AwaitDispatchEntryAsync(winner.Dispatcher, winningDrain);
            var winnerSessionId = await SqlServerBlockedRequestProbe.ReadSessionIdAsync(winner.Context);

            var challengingDrain = challenger.Processor.Process(challengerMessage);
            var blockedRequest = await SqlServerBlockedRequestProbe.WaitForRequestBlockedByAsync(harness.ConnectionString, winnerSessionId);

            winner.Dispatcher.Release();
            await winningDrain;
            await challengingDrain;

            // The substantive assertions come FIRST and the vacuity guard LAST, so a mutation reddens this fact at
            // the claim it makes rather than at the guard. A guard that fires first would leave every red saying
            // only that the race did not happen.
            winner.Dispatcher.DispatchedMessageIds.Should().ContainSingle().Which.Should().Be(harness.RacedMessageId,
                "the drain that took the claim is the one that publishes");
            challenger.Dispatcher.DispatchedMessageIds.Should().BeEmpty(
                "a denied drain publishes nothing, so the message the winner already put on the broker is not published twice");

            var racedMessage = await harness.ReadMessageAsync(harness.RacedMessageId);
            (racedMessage.NextAttemptAtUtc, racedMessage.ProcessedFromOutboxAtUtc.HasValue).Should().Be(
                (winnerMessage.NextAttemptAtUtc, true),
                "a drain claim that COMMITTED carries the processed stamp written with it: the row holds the instant the winner's claim wrote AND the stamp, so no poll can read it claimed but unstamped");
            racedMessage.DispatchAttempts.Should().Be(0,
                "a denied drain never attempted the dispatch, so the denial spends no attempt");

            blockedRequest.Should().NotBeNull(
                "the denial is only measured if the challenging drain reached the claim and waited on it");
        }

        // INVARIANT: the drain claim is exactly as durable as the unit of work carrying it, so a publish that fails
        // rolls the claim back and the drain waiting on that row's lock is GRANTED it. This is what taking the claim
        // inside the unit of work buys beyond arbitration: a drain that dies mid-publish hands the row back rather
        // than stranding it.
        // Oracle: this fact. Hoisting the claim OUT of the unit of work, so it survives the rollback that failure
        // caused, reddens it at "challenger.Dispatcher.DispatchedMessageIds to contain a single item, but the
        // collection is empty" - the drain waiting on the row is denied a message nothing ever published. That
        // mutation reddens all eight cases in this class on both target frameworks; this is the one whose red names
        // the rollback (measured by making the change and counting).
        [RequiresDockerTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MustGrantTheWaitingDrainsClaimOnceTheWinningDrainRollsBack(bool readCommittedSnapshot)
        {
            var harness = await CreateArbitrationHarnessAsync(readCommittedSnapshot);

            using var winner = harness.CreateDrain(parksOnDispatch: true);
            using var challenger = harness.CreateDrain(parksOnDispatch: false);
            winner.Dispatcher.FailureRaisedOnRelease = new InvalidOperationException("the broker refused the publish");

            var winnerMessage = await winner.PollAsync(harness.RacedMessageId);
            var challengerMessage = await challenger.PollAsync(harness.RacedMessageId);

            var winningDrain = winner.Processor.Process(winnerMessage);
            await AwaitDispatchEntryAsync(winner.Dispatcher, winningDrain);
            var winnerSessionId = await SqlServerBlockedRequestProbe.ReadSessionIdAsync(winner.Context);

            var challengingDrain = challenger.Processor.Process(challengerMessage);
            var blockedRequest = await SqlServerBlockedRequestProbe.WaitForRequestBlockedByAsync(harness.ConnectionString, winnerSessionId);

            winner.Dispatcher.Release();
            await winningDrain;
            await challengingDrain;

            // Substantive assertions first, vacuity guard last - see MustDenyTheWaitingDrainsClaimOnceTheWinningDrainCommits.
            challenger.Dispatcher.DispatchedMessageIds.Should().ContainSingle().Which.Should().Be(harness.RacedMessageId,
                "the rolled-back claim leaves the row unclaimed, so the drain waiting on it publishes the message");

            var racedMessage = await harness.ReadMessageAsync(harness.RacedMessageId);
            racedMessage.ProcessedFromOutboxAtUtc.Should().NotBeNull(
                "the drain that did publish the message stamped the row processed");

            blockedRequest.Should().NotBeNull(
                "the grant is only measured if the challenging drain reached the claim and waited on it");
        }

        // INVARIANT: the drain claim's predicate is a single-row seek on the outbox's Id primary key, so the block it
        // causes is ONE ROW DEEP. A drain holding one row through a slow publish leaves every other row claimable -
        // the whole design rests on this, because a fleet-wide block would let one wedged publish stall the outbox
        // for every drain. The caveat travels with the rule: a predicate that stopped being a seek and became a range
        // or a scan would take locks beyond the one row, and this fact is what would catch that.
        // Oracle: this fact. Dropping Id from the claim predicate, which turns the compare-and-set into table-level
        // contention, reddens THIS fact's two cases and no other fact in this class, at "publishedTheDifferentRow to
        // be True ... but found False" - the different row's drain ends up behind the raced row's lock. That
        // mutation also reddens the WhenClaimingForDispatch unit fact MustClaimOnlyTheMessageItWasHanded, three over
        // the 250-fact project (measured by making the change and counting).
        // The ordering is read from the server rather than timed: the raced row is observably blocked BEFORE the
        // different row's drain runs and STILL blocked AFTER it published, so the different row was claimable DURING
        // the block rather than after it lifted.
        [RequiresDockerTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MustLeaveADifferentRowClaimableWhileADrainIsBlockedOnTheRacedRow(bool readCommittedSnapshot)
        {
            var harness = await CreateArbitrationHarnessAsync(readCommittedSnapshot);

            using var winner = harness.CreateDrain(parksOnDispatch: true);
            using var challenger = harness.CreateDrain(parksOnDispatch: false);
            using var differentRowDrain = harness.CreateDrain(parksOnDispatch: false);

            var winnerMessage = await winner.PollAsync(harness.RacedMessageId);
            var challengerMessage = await challenger.PollAsync(harness.RacedMessageId);
            var differentMessage = await differentRowDrain.PollAsync(harness.DifferentMessageId);

            var winningDrain = winner.Processor.Process(winnerMessage);
            await AwaitDispatchEntryAsync(winner.Dispatcher, winningDrain);
            var winnerSessionId = await SqlServerBlockedRequestProbe.ReadSessionIdAsync(winner.Context);

            var challengingDrain = challenger.Processor.Process(challengerMessage);
            var blockedBeforeTheDifferentRow = await SqlServerBlockedRequestProbe.WaitForRequestBlockedByAsync(harness.ConnectionString, winnerSessionId);

            var differentRowPublish = differentRowDrain.Processor.Process(differentMessage);
            var settledWithinBudget = await Task.WhenAny(differentRowPublish, Task.Delay(DifferentRowClaimBudget));
            var publishedTheDifferentRow = ReferenceEquals(settledWithinBudget, differentRowPublish);

            var blockedAfterTheDifferentRow = await SqlServerBlockedRequestProbe.ReadRequestsBlockedByAsync(harness.ConnectionString, winnerSessionId);

            winner.Dispatcher.Release();
            await winningDrain;
            await challengingDrain;
            await differentRowPublish;

            // Substantive assertions first, vacuity guard last - see MustDenyTheWaitingDrainsClaimOnceTheWinningDrainCommits.
            publishedTheDifferentRow.Should().BeTrue(
                "the drain claiming a DIFFERENT row is not held by the lock the blocked drain waits on");
            differentRowDrain.Dispatcher.DispatchedMessageIds.Should().ContainSingle().Which.Should().Be(harness.DifferentMessageId,
                "the different row was granted its claim and published while the raced row was held");
            blockedAfterTheDifferentRow.Should().ContainSingle(
                "ONE request waits on the winning drain - the one racing its row. A second would mean the claim locked more than the row it names, and the different row's drain would be behind it too")
                .Which.Status.Should().Be("suspended",
                "the raced row was STILL held once the different row had published, so the different row was claimable during the block rather than after it lifted");
            blockedAfterTheDifferentRow[0].BlockingSessionId.Should().Be(winnerSessionId);

            blockedBeforeTheDifferentRow.Should().NotBeNull(
                "the claim on the raced row must already be blocked, or there is no block for the different row to be claimable during");
        }

        // Waits for the parked dispatch to be entered, and surfaces a drain that completed without ever reaching its
        // dispatch rather than waiting on a signal that can no longer arrive. OutboxProcessor.Process swallows its
        // failures, so a drain that never published completes quietly and this is what names it.
        private static async Task AwaitDispatchEntryAsync(ParkingInfrastructureDispatcher dispatcher, Task drain)
        {
            var settled = await Task.WhenAny(dispatcher.DispatchEntered, drain);
            if (!ReferenceEquals(settled, drain))
            {
                return;
            }

            await drain;
            throw new InvalidOperationException("The winning drain completed without entering its dispatch.");
        }

        // READ_COMMITTED_SNAPSHOT is set from a master connection on a database of this test case's OWN, before the
        // harness opens any pooled connection to it: the setting needs exclusive access to the database, which a
        // per-case database gives without disturbing a sibling test class sharing the container. It is stated
        // explicitly in BOTH directions rather than letting the OFF case inherit the server default, so the fact
        // names the setting it ran under.
        private async Task<DrainArbitrationHarness> CreateArbitrationHarnessAsync(bool readCommittedSnapshot)
        {
            var connectionString = await _fixture.CreateDatabaseAsync("ef_outbox_arbitration");
            await SetReadCommittedSnapshotAsync(connectionString, readCommittedSnapshot);

            var contexts = SqlServerOutboxContextHarness.Create(connectionString);
            var stagedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var racedMessage = CreateStagedMessage(stagedAtUtc);
            var differentMessage = CreateStagedMessage(stagedAtUtc.AddMinutes(1));

            using (var seedContext = contexts.CreateContext())
            {
                await seedContext.Set<OutboxMessage>().AddRangeAsync(racedMessage, differentMessage);
                await seedContext.SaveChangesAsync();
            }

            return new DrainArbitrationHarness(contexts, New, connectionString, racedMessage.MessageId, differentMessage.MessageId);
        }

        private async Task SetReadCommittedSnapshotAsync(string connectionString, bool readCommittedSnapshot)
        {
            var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            var setting = readCommittedSnapshot ? "ON" : "OFF";

            await using var master = new SqlConnection(_fixture.GetMasterConnectionString());
            await master.OpenAsync();

            await using var command = master.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT {setting} WITH ROLLBACK IMMEDIATE;";
            await command.ExecuteNonQueryAsync();
        }

        private OutboxMessage CreateStagedMessage(DateTime sentToOutboxAtUtc)
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.Destination = Destination;
            message.SentToOutboxAtUtc = sentToOutboxAtUtc;
            message.MessageContentType = JsonContentType;
            message.MessageBody = MessageBody;
            message.MessageContext = ChatterJson.Serialize(new Dictionary<string, object> { [MessageContext.InfrastructureType] = Infrastructure });
            return message;
        }

        private static ILoggerFactory CreateLoggerFactory()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            return loggerFactory.Object;
        }

        /// <summary>
        /// The seeded database and the drains that race over it. Every drain gets its OWN context, store, processor
        /// and messaging infrastructure, which is the per-poll scope the drain loop opens; sharing any of them would
        /// put both drains on one connection and there would be nothing to arbitrate.
        /// </summary>
        private sealed class DrainArbitrationHarness
        {
            private readonly SqlServerOutboxContextHarness _contexts;
            private readonly Testing.Core.INewContext _newContext;
            private readonly ReliabilityOptions _options = new ReliabilityOptions();

            public DrainArbitrationHarness(SqlServerOutboxContextHarness contexts,
                                           Testing.Core.INewContext newContext,
                                           string connectionString,
                                           string racedMessageId,
                                           string differentMessageId)
            {
                _contexts = contexts;
                _newContext = newContext;
                ConnectionString = connectionString;
                RacedMessageId = racedMessageId;
                DifferentMessageId = differentMessageId;
            }

            public string ConnectionString { get; }

            /// <summary>The row two drains poll and race for.</summary>
            public string RacedMessageId { get; }

            /// <summary>The row a third drain claims while the race is held, which must stay claimable throughout.</summary>
            public string DifferentMessageId { get; }

            public Drain CreateDrain(bool parksOnDispatch)
                => new Drain(_contexts.CreateContext(), _newContext, _options, parksOnDispatch);

            public async Task<OutboxMessage> ReadMessageAsync(string messageId)
            {
                using var context = _contexts.CreateContext();
                return await context.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.MessageId == messageId);
            }
        }

        /// <summary>
        /// One drain: its own context, its own store, its own Outbox Processor and its own messaging infrastructure.
        /// </summary>
        private sealed class Drain : IDisposable
        {
            private readonly BrokeredMessageOutbox<SqlServerOutboxContext> _outbox;

            public Drain(SqlServerOutboxContext context, Testing.Core.INewContext newContext, ReliabilityOptions options, bool parksOnDispatch)
            {
                Context = context;
                Dispatcher = new ParkingInfrastructureDispatcher { ParksOnDispatch = parksOnDispatch };
                _outbox = new BrokeredMessageOutbox<SqlServerOutboxContext>(context, CreateLoggerFactory(), options);
                Processor = new OutboxProcessor(CreateInfrastructureProvider(newContext, Dispatcher),
                                                newContext.Common().RecordingLogger<OutboxProcessor>().Creation,
                                                new BodyConverterFactory(new IBrokeredMessageBodyConverter[] { new JsonBodyConverter() }),
                                                _outbox,
                                                options);
            }

            public SqlServerOutboxContext Context { get; }

            public ParkingInfrastructureDispatcher Dispatcher { get; }

            public OutboxProcessor Processor { get; }

            // The REAL poll, so the value the drain claim compares against is the one a poll reported. Every drain in
            // a fact polls BEFORE any of them runs, which is what leaves them all holding the row's ORIGINAL next
            // attempt instant and racing rather than reading each other's claim.
            public async Task<OutboxMessage> PollAsync(string messageId)
            {
                var batch = await ((IPollableOutboxStore)_outbox).GetUnprocessedMessagesFromOutbox();
                return batch.Single(message => message.MessageId == messageId);
            }

            public void Dispose() => Context.Dispose();

            private static IMessagingInfrastructureProvider CreateInfrastructureProvider(Testing.Core.INewContext newContext, IMessagingInfrastructureDispatcher dispatcher)
                => new MessagingInfrastructureProvider(new IMessagingInfrastructure[] { new DispatchOnlyInfrastructure(Infrastructure, dispatcher) },
                                                       newContext.Common().RecordingLogger<MessagingInfrastructureProvider>().Creation);
        }

        /// <summary>
        /// The messaging infrastructure a drain publishes to. It records every message id it was handed - the sink
        /// the publication facts count - and, when it parks, holds the publish open so the drain's unit of work stays
        /// open with the drain claim inside it. Releasing it either lets the publish return or fails it, which is how
        /// the commit and the rollback are chosen.
        /// </summary>
        private sealed class ParkingInfrastructureDispatcher : IMessagingInfrastructureDispatcher
        {
            private readonly List<string> _dispatchedMessageIds = new List<string>();

            // RunContinuationsAsynchronously so releasing the park cannot resume the parked drain inline on the
            // thread that released it.
            private readonly TaskCompletionSource<bool> _dispatchEntered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> _dispatchReleased =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public bool ParksOnDispatch { get; set; }

            /// <summary>Raised out of the parked publish when it is released, which rolls the drain's unit of work back.</summary>
            public Exception FailureRaisedOnRelease { get; set; }

            public Task DispatchEntered => _dispatchEntered.Task;

            public IReadOnlyList<string> DispatchedMessageIds => _dispatchedMessageIds;

            public void Release() => _dispatchReleased.TrySetResult(true);

            public Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
                => throw new NotSupportedException("The outbox drain publishes one message per call and never uses the batch overload.");

            public async Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
            {
                _dispatchedMessageIds.Add(brokeredMessage.MessageId);

                if (!ParksOnDispatch)
                {
                    return;
                }

                _dispatchEntered.TrySetResult(true);
                await _dispatchReleased.Task;

                if (FailureRaisedOnRelease is not null)
                {
                    throw FailureRaisedOnRelease;
                }
            }
        }

        /// <summary>
        /// Registers the parking dispatcher under the Messaging Infrastructure type the persisted message names, so
        /// the REAL MessagingInfrastructureProvider performs the lookup the drain performs. The receive and
        /// path-building members are unreachable from a drain and say so rather than answering null.
        /// </summary>
        private sealed class DispatchOnlyInfrastructure : IMessagingInfrastructure
        {
            public DispatchOnlyInfrastructure(string type, IMessagingInfrastructureDispatcher dispatchInfrastructure)
            {
                Type = type;
                DispatchInfrastructure = dispatchInfrastructure;
            }

            public string Type { get; }
            public IMessagingInfrastructureDispatcher DispatchInfrastructure { get; }
            public IMessagingInfrastructureReceiver ReceiveInfrastructure => throw new NotSupportedException("The outbox drain never receives.");
            public IBrokeredMessagePathBuilder PathBuilder => throw new NotSupportedException("The outbox drain publishes to the persisted destination and builds no path.");
        }
    }
}
