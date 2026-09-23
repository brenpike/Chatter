using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    // CRITERION: the DRAIN CLAIM - the compare-and-set that arbitrates which of several drains running at once gets
    // to try a message - over a REAL relational store. The claim is a single statement whose whole behaviour lives
    // in SQL, so the EF Core InMemory provider, which evaluates a predicate in process and refuses ExecuteUpdate
    // outright, can say nothing about it. These facts drive the real BrokeredMessageOutbox over SQLite.
    //
    // The drain claim is a different thing from the claim the processed stamp carries: that one records that a
    // message was dispatched, this one only decides who may try.
    //
    // BOUND: SQLite cannot show one drain BLOCKING on another's uncommitted row - that needs a real server, and it
    // is measured over SQL Server elsewhere. What is measured here is the predicate: which observed values are
    // granted, which are refused, which rows are touched, and what SQL the two observed shapes emit.
    //
    // BOUND: SqliteOutboxContext keys the outbox on MessageId and maps no concurrency token, so the Id conjunct
    // below is an ordinary column comparison here rather than the single-row primary-key seek the production
    // OutboxMessageConfiguration makes it. Which rows the predicate selects is the same either way, and that is
    // what these facts assert; what the seek buys - locks confined to one row - is a property of the server.
    public class WhenClaimingForDispatch : Testing.Core.Context, IAsyncDisposable
    {
        // A next attempt instant in the past, so the poll's due gate hands the message back rather than holding it.
        private static readonly DateTime ObservedNextAttempt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime FirstClaimedNextAttempt = ObservedNextAttempt.AddMinutes(5);
        private static readonly DateTime SecondClaimedNextAttempt = ObservedNextAttempt.AddMinutes(9);

        private readonly SqliteOutboxContextHarness _harness = SqliteOutboxContextHarness.Create();

        // EXACTLY ONE DRAIN IS GRANTED. Two drains poll the same message, so both carry the same observed next
        // attempt; the first moves it and the second finds the value it observed is no longer there. The stored
        // instant is asserted to be the WINNER'S, not merely "not the observed one", so a loser that wrote and was
        // told it lost would still be caught.
        [Fact]
        public async Task MustGrantTheDrainClaimToOnlyOneOfTwoDrainsThatObservedTheSameNextAttempt()
        {
            var staged = SeedMessage(1, "message-two-drains-poll", ObservedNextAttempt);

            using var firstContext = _harness.CreateContext();
            using var secondContext = _harness.CreateContext();
            var firstDrain = CreateStore(firstContext);
            var secondDrain = CreateStore(secondContext);

            var firstPolled = (await firstDrain.GetUnprocessedMessagesFromOutbox()).Single();
            var secondPolled = (await secondDrain.GetUnprocessedMessagesFromOutbox()).Single();

            var firstGranted = await firstDrain.TryClaimForDispatch(firstPolled, firstPolled.NextAttemptAtUtc, FirstClaimedNextAttempt);
            var secondGranted = await secondDrain.TryClaimForDispatch(secondPolled, secondPolled.NextAttemptAtUtc, SecondClaimedNextAttempt);

            firstGranted.Should().BeTrue("the first drain to reach the row observed the value the row still held");
            secondGranted.Should().BeFalse("the value the second drain observed had already been moved by the first");
            ReadStored(staged.MessageId).NextAttemptAtUtc.Should().Be(FirstClaimedNextAttempt,
                "the refused drain must leave the winner's claim exactly as the winner wrote it");
        }

        // A NULL OBSERVED VALUE IS COMPARED AS A LITERAL IS NULL. A staged message is due now and carries no next
        // attempt instant, so the commonest claim of all observes null - and a null compared through a PARAMETER
        // matches no row in SQL, which would refuse every drain on every staged message and drain nothing. EF keys
        // its query cache on parameter nullability and emits the literal instead; the SQL is captured rather than
        // inferred from the outcome, because a granted claim alone cannot tell the two apart from outside.
        [Fact]
        public async Task MustCompareANullObservedNextAttemptAsIsNull()
        {
            var staged = SeedMessage(1, "message-staged-and-due-now", nextAttemptAtUtc: null);
            var captured = new CapturedUpdateStatements();

            using var context = _harness.CreateContext(options => options.AddInterceptors(captured));
            var drain = CreateStore(context);
            var polled = (await drain.GetUnprocessedMessagesFromOutbox()).Single();

            var granted = await drain.TryClaimForDispatch(polled, polled.NextAttemptAtUtc, FirstClaimedNextAttempt);

            granted.Should().BeTrue("a staged message observed as never-attempted is claimable");
            PredicateOf(captured.Statements.Single()).Should().Contain("\"NextAttemptAtUtc\" IS NULL",
                "a null observed value compared through a parameter would match no row and refuse every drain");
            ReadStored(staged.MessageId).NextAttemptAtUtc.Should().Be(FirstClaimedNextAttempt);
        }

        // A NON-NULL OBSERVED VALUE IS COMPARED AS A PARAMETER. The counterpart of the fact above: the same
        // expression must NOT collapse to IS NULL once the observed value is present, which is what would happen if
        // the comparison were written against a captured constant rather than the supplied parameter.
        [Fact]
        public async Task MustCompareANonNullObservedNextAttemptAsAParameter()
        {
            SeedMessage(1, "message-with-an-observed-instant", ObservedNextAttempt);
            var captured = new CapturedUpdateStatements();

            using var context = _harness.CreateContext(options => options.AddInterceptors(captured));
            var drain = CreateStore(context);
            var polled = (await drain.GetUnprocessedMessagesFromOutbox()).Single();

            var granted = await drain.TryClaimForDispatch(polled, polled.NextAttemptAtUtc, FirstClaimedNextAttempt);

            granted.Should().BeTrue();
            // The PREDICATE alone, never the whole statement: the SET clause assigns the same column from a
            // parameter too, so a statement-wide search would be satisfied by the assignment and would pass with no
            // comparison in the predicate at all.
            var predicate = PredicateOf(captured.Statements.Single());
            predicate.Should().NotContain("\"NextAttemptAtUtc\" IS NULL",
                "the observed value is present, so the comparison must be against it rather than against null");
            predicate.Should().Contain("\"NextAttemptAtUtc\" = @",
                "the observed value must reach the database as a parameter");
        }

        // THE CLAIM TOUCHES ONE ROW. Two messages are staged carrying the SAME observed next attempt, so a
        // predicate missing its identity conjunct would match both and be granted on both. The untouched row is read
        // back through a fresh context, so what is asserted is what the database stores.
        [Fact]
        public async Task MustClaimOnlyTheMessageItWasHanded()
        {
            var handed = SeedMessage(1, "message-handed-to-the-drain", ObservedNextAttempt);
            var bystander = SeedMessage(2, "message-no-drain-took", ObservedNextAttempt);

            using var context = _harness.CreateContext();
            var drain = CreateStore(context);
            var polled = (await drain.GetUnprocessedMessagesFromOutbox()).Single(message => message.MessageId == handed.MessageId);

            var granted = await drain.TryClaimForDispatch(polled, polled.NextAttemptAtUtc, FirstClaimedNextAttempt);

            granted.Should().BeTrue();
            ReadStored(handed.MessageId).NextAttemptAtUtc.Should().Be(FirstClaimedNextAttempt);
            ReadStored(bystander.MessageId).NextAttemptAtUtc.Should().Be(ObservedNextAttempt,
                "a message no drain claimed must keep the instant it was staged with, or a drain that never polled it would push it out");
        }

        // A PROCESSED MESSAGE IS REFUSED. The row was dispatched between the poll that handed it back and the claim,
        // so its observed next attempt still matches - only the processed stamp separates the two states. Without
        // that conjunct the claim would be granted and the message published a second time.
        [Fact]
        public async Task MustRefuseTheDrainClaimOnAMessageAlreadyProcessed()
        {
            var processed = SeedMessage(1, "message-another-drain-dispatched", ObservedNextAttempt, processedFromOutboxAtUtc: DateTime.UtcNow);

            using var context = _harness.CreateContext();
            var drain = CreateStore(context);

            var granted = await drain.TryClaimForDispatch(ReadStored(processed.MessageId), ObservedNextAttempt, FirstClaimedNextAttempt);

            granted.Should().BeFalse("a message that has already been dispatched is not a message to arbitrate over");
            ReadStored(processed.MessageId).NextAttemptAtUtc.Should().Be(ObservedNextAttempt,
                "a refused claim writes nothing");
        }

        // A STALE OBSERVED VALUE IS REFUSED. The claim reports what THIS caller observed, not what the row says by
        // the time the claim runs, so a drain holding a value the row has moved past is told it did not win.
        [Fact]
        public async Task MustRefuseTheDrainClaimAgainstAStaleObservedNextAttempt()
        {
            var staged = SeedMessage(1, "message-whose-instant-moved-on", ObservedNextAttempt);

            using var context = _harness.CreateContext();
            var drain = CreateStore(context);

            var granted = await drain.TryClaimForDispatch(ReadStored(staged.MessageId),
                                                          ObservedNextAttempt.AddMinutes(-1),
                                                          FirstClaimedNextAttempt);

            granted.Should().BeFalse("the value observed is not the value the row holds");
            ReadStored(staged.MessageId).NextAttemptAtUtc.Should().Be(ObservedNextAttempt,
                "a refused claim writes nothing");
        }

        // THE GRANTED CLAIM IS WRITTEN ONTO THE SUPPLIED MESSAGE. The claim reaches the row outside the change
        // tracker, so the tracked instance would otherwise keep the value the poll loaded - and a later tracked
        // Update, which marks every property modified, would then restate that stale instant and revert the claim
        // inside the very transaction that took it.
        [Fact]
        public async Task MustWriteTheGrantedClaimOntoTheSuppliedMessage()
        {
            SeedMessage(1, "message-the-drain-still-holds", ObservedNextAttempt);

            using var context = _harness.CreateContext();
            var drain = CreateStore(context);
            var polled = (await drain.GetUnprocessedMessagesFromOutbox()).Single();

            var granted = await drain.TryClaimForDispatch(polled, polled.NextAttemptAtUtc, FirstClaimedNextAttempt);

            granted.Should().BeTrue();
            polled.NextAttemptAtUtc.Should().Be(FirstClaimedNextAttempt,
                "a tracked save that restated the polled instant would undo the claim this drain was just granted");
        }

        // THE CLAIM RUNS INSIDE THE CALLER'S TRANSACTION. Its caller takes the claim inside the drain's unit of
        // work, and the relational arbitration depends on the statement enlisting in that transaction rather than
        // committing on its own - that enlistment is what makes a losing drain wait on the row lock over a real
        // server. Rolling the unit of work back is what shows it: a claim that had committed independently would
        // survive the rollback.
        [Fact]
        public async Task MustEnlistTheClaimInTheAmbientTransaction()
        {
            var staged = SeedMessage(1, "message-claimed-then-rolled-back", ObservedNextAttempt);
            var captured = new CapturedUpdateStatements();

            using var context = _harness.CreateContext(options => options.AddInterceptors(captured));
            var outbox = new BrokeredMessageOutbox<SqliteOutboxContext>(context, CreateLoggerFactory());
            var drain = (IPollableOutboxStore)outbox;
            var polled = (await drain.GetUnprocessedMessagesFromOutbox()).Single();
            var granted = false;

            Func<Task> abandonedUnitOfWork = () => ((IUnitOfWork)outbox).ExecuteAsync(async ct =>
            {
                granted = await drain.TryClaimForDispatch(polled, polled.NextAttemptAtUtc, FirstClaimedNextAttempt, ct);
                throw new InvalidOperationException("the unit of work that took the claim did not reach its commit");
            }, null);

            await abandonedUnitOfWork.Should().ThrowAsync<InvalidOperationException>();

            granted.Should().BeTrue("the fact proves nothing unless the claim was taken before the rollback");
            captured.Statements.Should().ContainSingle(
                "the fact proves nothing unless a statement genuinely reached the database inside the unit of work");
            ReadStored(staged.MessageId).NextAttemptAtUtc.Should().Be(ObservedNextAttempt,
                "a claim that outlived the rolled-back unit of work was never in its transaction");
        }

        private OutboxMessage SeedMessage(int id, string messageId, DateTime? nextAttemptAtUtc, DateTime? processedFromOutboxAtUtc = null)
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.Id = id;
            message.MessageId = messageId;
            message.NextAttemptAtUtc = nextAttemptAtUtc;
            message.ProcessedFromOutboxAtUtc = processedFromOutboxAtUtc;

            using var seedContext = _harness.CreateContext();
            seedContext.Add(message);
            seedContext.SaveChanges();

            return message;
        }

        private static string PredicateOf(string statement)
        {
            var whereAt = statement.IndexOf("WHERE", StringComparison.Ordinal);
            whereAt.Should().BeGreaterThan(-1, "a claim with no predicate at all would match every row in the outbox");

            return statement.Substring(whereAt);
        }

        private IPollableOutboxStore CreateStore(SqliteOutboxContext context)
            => new BrokeredMessageOutbox<SqliteOutboxContext>(context, CreateLoggerFactory());

        private OutboxMessage ReadStored(string messageId)
        {
            using var context = _harness.CreateContext();

            return context.Set<OutboxMessage>().AsNoTracking().Single(stored => stored.MessageId == messageId);
        }

        private static ILoggerFactory CreateLoggerFactory()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            return loggerFactory.Object;
        }

        public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

        private sealed class CapturedUpdateStatements : DbCommandInterceptor
        {
            private readonly List<string> _statements = new List<string>();

            public IReadOnlyList<string> Statements => _statements;

            public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
                                                                                      CommandEventData eventData,
                                                                                      InterceptionResult<int> result,
                                                                                      CancellationToken cancellationToken = default)
            {
                if (command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                {
                    _statements.Add(command.CommandText);
                }

                return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
            }
        }
    }
}
