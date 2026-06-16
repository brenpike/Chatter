using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    // (MessageId primary key). The MessageId PK is the backstop behind BrokeredMessageInbox's read-then-add: even
    // when two receivers both pass the AnyAsync check, the unique constraint admits exactly one row.
    [Trait("Category", "Integration")]
    [Collection(EfReliabilitySqlServerCollection.Name)]
    public class WhenDeduplicatingInboxOnSqlServer
    {
        // SQL Server error number for a primary-key / unique-constraint violation.
        private const int PrimaryKeyViolationNumber = 2627;

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

        [RequiresDockerFact]
        public async Task MustPersistExactlyOneInboxRowWhenTwoReceiversRaceForTheSameMessageId()
        {
            var harness = await CreateHarnessAsync();
            var messageId = Guid.NewGuid().ToString();

            using var firstContext = harness.CreateContext();
            using var secondContext = harness.CreateContext();
            var firstInbox = new BrokeredMessageInbox<SqlServerOutboxContext>(firstContext, CreateLogger(), new ReliabilityOptions());
            var secondInbox = new BrokeredMessageInbox<SqlServerOutboxContext>(secondContext, CreateLogger(), new ReliabilityOptions());

            // ReceiveViaInbox only AddAsync's to the change tracker; it does NOT SaveChanges. Each receiver must
            // explicitly save to hit the DB constraint. With both contexts having passed the AnyAsync check before
            // either saved, exactly one SaveChanges commits and the other violates the MessageId PK.
            await firstInbox.ReceiveViaInbox("payload", CreateBrokerContext(messageId), () => Task.CompletedTask);
            await secondInbox.ReceiveViaInbox("payload", CreateBrokerContext(messageId), () => Task.CompletedTask);

            await firstContext.SaveChangesAsync();

            Func<Task> secondSave = () => secondContext.SaveChangesAsync();
            var thrown = await secondSave.Should().ThrowAsync<DbUpdateException>();
            thrown.WithInnerException<SqlException>()
                .Which.Number.Should().Be(PrimaryKeyViolationNumber,
                    "the losing receiver must lose specifically at the MessageId primary-key constraint (SQL Server error 2627)");

            using var verifyContext = harness.CreateContext();
            var rows = await verifyContext.Set<InboxMessage>().Where(m => m.MessageId == messageId).ToListAsync();
            rows.Should().HaveCount(1, "the MessageId primary key admits exactly one inbox row");
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
    }
}
