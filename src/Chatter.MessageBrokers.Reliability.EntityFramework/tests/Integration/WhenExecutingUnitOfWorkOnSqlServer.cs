using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.MessageBrokers;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
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
    // CRITERIA 2 + 4: outbox atomicity and unit-of-work lifecycle, proven over REAL SQL Server transactions via
    // BrokeredMessageOutbox<SqlServerOutboxContext> acting as IUnitOfWork. Mirrors the assertions in
    // WhenExecutingUnitOfWorkOverSqlite.cs but against the production model on a real SQL Server database.
    [Trait("Category", "Integration")]
    [Collection(EfReliabilitySqlServerCollection.Name)]
    public class WhenExecutingUnitOfWorkOnSqlServer : Testing.Core.Context
    {
        private readonly EfReliabilitySqlServerFixture _fixture;

        public WhenExecutingUnitOfWorkOnSqlServer(EfReliabilitySqlServerFixture fixture)
            => _fixture = fixture;

        // (a) commit persists the staged domain write together with the UoW; reload in a fresh context sees it.
        [RequiresDockerFact]
        public async Task MustPersistOutboxMessageWhenOperationCommits()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var sut = CreateUnitOfWork(context);

            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            await sut.ExecuteAsync(async ct =>
            {
                await context.Set<OutboxMessage>().AddAsync(message, ct);
            }, null);

            sut.HasActiveTransaction.Should().BeFalse();

            using var freshContext = harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().NotBeNull();
        }

        // (b) an operation throw rolls the staged domain write back; reload sees nothing.
        [RequiresDockerFact]
        public async Task MustDiscardOutboxMessageWhenOperationThrows()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var sut = CreateUnitOfWork(context);

            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => sut.ExecuteAsync(async ct =>
            {
                await context.Set<OutboxMessage>().AddAsync(message, ct);
                throw operationException;
            }, null);

            await act.Should().ThrowAsync<InvalidOperationException>();

            sut.HasActiveTransaction.Should().BeFalse();

            using var freshContext = harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().BeNull();
        }

        // (c) a SendToOutbox enqueue inside a UoW commits atomically with a co-staged domain entity. Both the
        // hand-staged outbox row and the SendToOutbox-enqueued row are present after commit.
        [RequiresDockerFact]
        public async Task MustCommitSendToOutboxEnqueueAtomicallyWithCoStagedEntity()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var store = new BrokeredMessageOutbox<SqlServerOutboxContext>(context, CreateLoggerFactory());
            var sut = (IUnitOfWork)store;

            OutboxMessage coStaged = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            var enqueuedMessageId = Guid.NewGuid().ToString();
            var transactionContext = new TransactionContext("receiver");

            await sut.ExecuteAsync(async ct =>
            {
                await context.Set<OutboxMessage>().AddAsync(coStaged, ct);
                await store.SendToOutbox(
                    new[] { CreateOutboundMessage(enqueuedMessageId) },
                    transactionContext,
                    ct);
            }, transactionContext);

            sut.HasActiveTransaction.Should().BeFalse();

            using var freshContext = harness.CreateContext();
            var coStagedPersisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == coStaged.MessageId);
            var enqueuedPersisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == enqueuedMessageId);

            coStagedPersisted.Should().NotBeNull("the co-staged domain write commits atomically with the enqueue");
            enqueuedPersisted.Should().NotBeNull("the SendToOutbox enqueue commits atomically with the co-staged write");
        }

        // (d) when a transaction is already open on the context, BeginAsync returns the existing CurrentTransaction
        // rather than starting a second one. The operation observes the pre-existing TransactionId.
        [RequiresDockerFact]
        public async Task MustReuseExistingTransactionWhenOneIsAlreadyOpen()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var sut = CreateUnitOfWork(context);

            await using var existingTransaction = await context.Database.BeginTransactionAsync();
            var existingTransactionId = existingTransaction.TransactionId;

            Guid observedTransactionId = Guid.Empty;

            await sut.ExecuteAsync(_ =>
            {
                observedTransactionId = sut.CurrentTransaction.TransactionId;
                return Task.CompletedTask;
            }, null);

            observedTransactionId.Should().Be(existingTransactionId);
            observedTransactionId.Should().NotBe(Guid.Empty);
        }

        // (e) the TransactionContext.Container is populated with the IPersistanceTransaction and CurrentTransactionId.
        [RequiresDockerFact]
        public async Task MustPopulateTransactionContextContainerWhenContextProvided()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var sut = CreateUnitOfWork(context);

            var transactionContext = new TransactionContext("receiver");

            Guid transactionIdInsideOperation = Guid.Empty;

            await sut.ExecuteAsync(_ =>
            {
                transactionIdInsideOperation = sut.CurrentTransaction.TransactionId;
                return Task.CompletedTask;
            }, transactionContext);

            var containedTransaction = transactionContext.Container.GetOrDefault<IPersistanceTransaction>();
            containedTransaction.Should().NotBeNull();

            var containedTransactionId = transactionContext.Container.Get<Guid>("CurrentTransactionId");
            containedTransactionId.Should().Be(transactionIdInsideOperation);
            containedTransactionId.Should().NotBe(Guid.Empty);
        }

        private async Task<SqlServerOutboxContextHarness> CreateHarnessAsync()
        {
            var connectionString = await _fixture.CreateDatabaseAsync("ef_outbox_uow");
            return SqlServerOutboxContextHarness.Create(connectionString);
        }

        private static IUnitOfWork CreateUnitOfWork(SqlServerOutboxContext context)
            => new BrokeredMessageOutbox<SqlServerOutboxContext>(context, CreateLoggerFactory());

        private static OutboundBrokeredMessage CreateOutboundMessage(string messageId)
            => new OutboundBrokeredMessage(
                messageId,
                Array.Empty<byte>(),
                new Dictionary<string, object>(),
                "destination",
                new TextPlainBodyConverter());

        private static ILoggerFactory CreateLoggerFactory()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            return loggerFactory.Object;
        }
    }
}
