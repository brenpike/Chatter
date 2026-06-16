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
    // unprocessed row, both try to claim it (UpdateProcessedDate), and exactly one commits while the loser throws
    // DbUpdateConcurrencyException (the rethrow at BrokeredMessageOutbox.SaveOutboxAsync).
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
            await firstStore.UpdateProcessedDate(firstMessage);

            // Second claim races on the same now-stale row and must lose with a concurrency exception.
            Func<Task> secondClaim = () => secondStore.UpdateProcessedDate(secondMessage);
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

                await firstStore.UpdateProcessedDate(firstMessage);

                Func<Task> secondClaim = () => secondStore.UpdateProcessedDate(secondMessage);
                await secondClaim.Should().ThrowAsync<DbUpdateConcurrencyException>();
            }

            // Reload in a fresh context: the row is processed exactly once (single claim, no silent double-dispatch).
            using var verifyContext = harness.CreateContext();
            var reloaded = await verifyContext.Set<OutboxMessage>().SingleAsync(m => m.MessageId == messageId);
            reloaded.ProcessedFromOutboxAtUtc.Should().NotBeNull();
        }

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
