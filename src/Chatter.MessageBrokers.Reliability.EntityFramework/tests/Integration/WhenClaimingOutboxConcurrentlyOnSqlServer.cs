using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.MessageBrokers;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Integration
{
    // CRITERION 1: exactly-once outbox claim under optimistic concurrency, proven over a real SQL Server database
    // with the PRODUCTION model (Id PK + ProcessedFromOutboxAtUtc concurrency token). Two stores load the SAME
    // unprocessed row, both stage a claim (UpdateProcessedDate) inside their own unit of work, and exactly one
    // commits while the loser throws DbUpdateConcurrencyException (the rethrow at
    // BrokeredMessageOutbox's IUnitOfWork.ExecuteAsync).
    [Trait("Category", "Integration")]
    [Collection(EfReliabilitySqlServerCollection.Name)]
    public class WhenClaimingOutboxConcurrentlyOnSqlServer : Testing.Core.Context
    {
        private readonly EfReliabilitySqlServerFixture _fixture;

        public WhenClaimingOutboxConcurrentlyOnSqlServer(EfReliabilitySqlServerFixture fixture)
            => _fixture = fixture;

        [RequiresDockerFact]
        public async Task MustAllowExactlyOneClaimWhenTwoStoresRaceForTheSameRow()
        {
            var (harness, messageId) = await CreateHarnessWithOneUnprocessedMessageAsync();

            // Two separate contexts/stores over the same database.
            using var firstContext = harness.CreateContext();
            using var secondContext = harness.CreateContext();
            var firstStore = new BrokeredMessageOutbox<SqlServerOutboxContext>(firstContext, CreateLoggerFactory());
            var secondStore = new BrokeredMessageOutbox<SqlServerOutboxContext>(secondContext, CreateLoggerFactory());

            // CRITICAL ORDERING: BOTH contexts must load the row at its original ProcessedFromOutboxAtUtc == null
            // value BEFORE either saves. Loading after the other has committed would observe the new concurrency
            // token and there would be no conflict.
            var firstMessage = await firstContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);
            var secondMessage = await secondContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);

            firstMessage.ProcessedFromOutboxAtUtc.Should().BeNull();
            secondMessage.ProcessedFromOutboxAtUtc.Should().BeNull();

            // First claim commits.
            await ClaimAsync(firstStore, firstMessage);

            // Second claim races on the same now-stale row and must lose with a concurrency exception.
            Func<Task> secondClaim = () => ClaimAsync(secondStore, secondMessage);
            await secondClaim.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        [RequiresDockerFact]
        public async Task MustClaimRowExactlyOnceWhenTwoStoresRaceForTheSameRow()
        {
            var (harness, messageId) = await CreateHarnessWithOneUnprocessedMessageAsync();

            using (var firstContext = harness.CreateContext())
            using (var secondContext = harness.CreateContext())
            {
                var firstStore = new BrokeredMessageOutbox<SqlServerOutboxContext>(firstContext, CreateLoggerFactory());
                var secondStore = new BrokeredMessageOutbox<SqlServerOutboxContext>(secondContext, CreateLoggerFactory());

                var firstMessage = await firstContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);
                var secondMessage = await secondContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);

                await ClaimAsync(firstStore, firstMessage);

                Func<Task> secondClaim = () => ClaimAsync(secondStore, secondMessage);
                await secondClaim.Should().ThrowAsync<DbUpdateConcurrencyException>();
            }

            // Reload in a fresh context: the processed stamp committed exactly once, because the concurrency token
            // rejected the loser's claim. This pins the single CLAIM only - ClaimAsync drives no dispatcher, and the
            // real drain publishes to the broker before it stamps, so duplicate publication is outside what this proves.
            using var verifyContext = harness.CreateContext();
            var reloaded = await verifyContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);
            reloaded.ProcessedFromOutboxAtUtc.Should().NotBeNull();
        }

        [RequiresDockerFact]
        public async Task MustSurfaceConcurrencyExceptionWhenConflictedRowWasDeleted()
        {
            var (harness, messageId) = await CreateHarnessWithOneUnprocessedMessageAsync();

            using var deletingContext = harness.CreateContext();
            using var claimingContext = harness.CreateContext();
            var claimingStore = new BrokeredMessageOutbox<SqlServerOutboxContext>(claimingContext, CreateLoggerFactory());

            // CRITICAL ORDERING: the claiming context must load the row BEFORE the deleting context removes it, so the
            // claim races against a row that no longer exists.
            var claimedMessage = await claimingContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);

            var doomedMessage = await deletingContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);
            deletingContext.Set<OutboxMessage>().Remove(doomedMessage);
            await deletingContext.SaveChangesAsync();

            // The compensating read finds no database values for the deleted row. The ORIGINAL concurrency exception
            // must still be what propagates.
            Func<Task> raceLoser = () => ClaimAsync(claimingStore, claimedMessage);
            await raceLoser.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        [RequiresDockerFact]
        public async Task MustSurfaceConcurrencyExceptionWhenCompensatingReadThrows()
        {
            var (harness, messageId) = await CreateHarnessWithOneUnprocessedMessageAsync();

            var failingRead = new FailingCompensationReadInterceptor();
            using var winningContext = harness.CreateContext();
            using var losingContext = harness.CreateContext(failingRead);
            var winningStore = new BrokeredMessageOutbox<SqlServerOutboxContext>(winningContext, CreateLoggerFactory());
            var losingStore = new BrokeredMessageOutbox<SqlServerOutboxContext>(losingContext, CreateLoggerFactory());

            // CRITICAL ORDERING: both contexts must load the row at its original ProcessedFromOutboxAtUtc == null
            // value BEFORE either saves, otherwise there is no conflict to compensate for.
            var winningMessage = await winningContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);
            var losingMessage = await losingContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);

            await ClaimAsync(winningStore, winningMessage);

            // The losing context's compensating read fails on a degraded connection. The ORIGINAL concurrency
            // exception must still be what propagates - the read's own failure must not replace it.
            Func<Task> raceLoser = () => ClaimAsync(losingStore, losingMessage);
            await raceLoser.Should().ThrowAsync<DbUpdateConcurrencyException>();

            // WITHOUT THIS THE TEST IS VACUOUS: a compensating read that was never made to fail also ends in
            // DbUpdateConcurrencyException, so the assertion above passes whether or not the guard was exercised.
            // Assert the fault was actually injected, exactly once, for the single conflicted entry.
            failingRead.InjectedFailureCount.Should().Be(1,
                "the guard is only proven if the compensating read was actually made to fail");
        }

        // The store stages the claim; the save that races is the unit of work's own CompleteAsync, so a claim must be
        // driven through IUnitOfWork.ExecuteAsync for the concurrency conflict to surface at all.
        private static Task ClaimAsync(BrokeredMessageOutbox<SqlServerOutboxContext> store, OutboxMessage message)
            => ((IUnitOfWork)store).ExecuteAsync(ct => store.UpdateProcessedDate(message, ct), null, default);

        private async Task<(SqlServerOutboxContextHarness Harness, string MessageId)> CreateHarnessWithOneUnprocessedMessageAsync()
        {
            var connectionString = await _fixture.CreateDatabaseAsync("ef_outbox_claim");
            var harness = SqlServerOutboxContextHarness.Create(connectionString);

            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            using var seedContext = harness.CreateContext();
            await seedContext.Set<OutboxMessage>().AddAsync(message);
            await seedContext.SaveChangesAsync();

            return (harness, message.MessageId);
        }

        private static ILoggerFactory CreateLoggerFactory()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            return loggerFactory.Object;
        }
    }
}
