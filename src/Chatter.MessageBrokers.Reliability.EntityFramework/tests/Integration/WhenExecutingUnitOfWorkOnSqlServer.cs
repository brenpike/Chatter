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

        // (f) a transaction the unit of work did not begin is never committed by it. The owner's rollback after
        // ExecuteAsync returns discards the staged row, which would be impossible had ExecuteAsync committed it.
        // INVARIANT: the owner's rollback is what makes this observable. A fresh context cannot read the staged row
        // while the adopted transaction is still open - the uncommitted write holds locks under READ COMMITTED, so
        // such a read blocks rather than returning nothing.
        [RequiresDockerFact]
        public async Task MustNotCommitAdoptedTransactionWhenOperationSucceeds()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var sut = CreateUnitOfWork(context);

            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            await using var existingTransaction = await context.Database.BeginTransactionAsync();

            await sut.ExecuteAsync(async ct =>
            {
                await context.Set<OutboxMessage>().AddAsync(message, ct);
            }, null);

            sut.HasActiveTransaction.Should().BeTrue("the unit of work did not begin the transaction, so it must leave it open for its owner");

            await existingTransaction.RollbackAsync();

            using var freshContext = harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().BeNull("the owner's rollback discards the staged row, which is only possible if the unit of work never committed it");
        }

        // (g) dispose path: an adopted transaction is still usable after ExecuteAsync returns, and the row the
        // operation staged was flushed into it by the unit of work's SaveChangesAsync, so the owner's commit persists it.
        [RequiresDockerFact]
        public async Task MustLeaveAdoptedTransactionUsableAfterOperationSucceeds()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var sut = CreateUnitOfWork(context);

            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            await using var existingTransaction = await context.Database.BeginTransactionAsync();

            await sut.ExecuteAsync(async ct =>
            {
                await context.Set<OutboxMessage>().AddAsync(message, ct);
            }, null);

            Func<Task> commit = () => existingTransaction.CommitAsync();
            await commit.Should().NotThrowAsync("the unit of work must not dispose a transaction it did not begin");

            using var freshContext = harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().NotBeNull("SaveChangesAsync still flushed the staged row into the adopted transaction");
        }

        // (h) an operation throw does NOT roll an adopted transaction back. The original exception instance
        // propagates and the owner's own earlier write survives its commit.
        [RequiresDockerFact]
        public async Task MustNotRollBackAdoptedTransactionWhenOperationThrows()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var sut = CreateUnitOfWork(context);

            OutboxMessage ownerMessage = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            var operationException = new InvalidOperationException("operation failed");

            await using var existingTransaction = await context.Database.BeginTransactionAsync();
            await context.Set<OutboxMessage>().AddAsync(ownerMessage);
            await context.SaveChangesAsync();

            Func<Task> act = () => sut.ExecuteAsync(_ => throw operationException, null);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().BeSameAs(operationException);

            sut.HasActiveTransaction.Should().BeTrue("the unit of work must not close a transaction it did not begin");

            await existingTransaction.CommitAsync();

            using var freshContext = harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == ownerMessage.MessageId);
            persisted.Should().NotBeNull("the owner's write survives because the unit of work did not roll its transaction back");
        }

        // (i) a nested unit of work over the same context leaves the outer transaction open and uncommitted once
        // both units of work return.
        [RequiresDockerFact]
        public async Task MustLeaveOuterTransactionOpenWhenANestedUnitOfWorkCompletes()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var sut = CreateUnitOfWork(context);

            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            await using var existingTransaction = await context.Database.BeginTransactionAsync();
            var existingTransactionId = existingTransaction.TransactionId;

            var hasActiveTransactionAfterNested = false;
            Guid observedTransactionIdAfterNested = Guid.Empty;

            await sut.ExecuteAsync(async outerCancellationToken =>
            {
                await sut.ExecuteAsync(async nestedCancellationToken =>
                {
                    await context.Set<OutboxMessage>().AddAsync(message, nestedCancellationToken);
                }, null, outerCancellationToken);

                hasActiveTransactionAfterNested = sut.HasActiveTransaction;
                observedTransactionIdAfterNested = sut.CurrentTransaction.TransactionId;
            }, null);

            hasActiveTransactionAfterNested.Should().BeTrue("a nested unit of work must not close the transaction the outer one is running in");
            observedTransactionIdAfterNested.Should().Be(existingTransactionId);

            sut.HasActiveTransaction.Should().BeTrue();
            context.Database.CurrentTransaction.TransactionId.Should().Be(existingTransactionId);

            await existingTransaction.RollbackAsync();

            using var freshContext = harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().BeNull("the outer transaction was still uncommitted, so its owner's rollback discards the nested write");
        }

        // (j) the #480 combination: the OUTER unit of work begins the transaction and the NESTED one adopts it.
        // Fact (i) pre-opens the transaction, so both of its levels are adopters and this pairing never runs there.
        // The outer invocation owns the transaction, so its commit is the single commit point for all three writes.
        [RequiresDockerFact]
        public async Task MustCommitOuterTransactionWhenANestedUnitOfWorkAdoptsIt()
        {
            var harness = await CreateHarnessAsync();
            using var context = harness.CreateContext();
            var sut = CreateUnitOfWork(context);

            OutboxMessage outerMessage = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            OutboxMessage nestedMessage = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            OutboxMessage postNestedMessage = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            Guid outerTransactionId = Guid.Empty;
            Guid nestedTransactionId = Guid.Empty;
            var hasActiveTransactionAfterNested = false;
            Guid? ambientTransactionIdAfterNested = null;

            await sut.ExecuteAsync(async outerCancellationToken =>
            {
                await context.Set<OutboxMessage>().AddAsync(outerMessage, outerCancellationToken);
                outerTransactionId = sut.CurrentTransaction.TransactionId;

                await sut.ExecuteAsync(async nestedCancellationToken =>
                {
                    await context.Set<OutboxMessage>().AddAsync(nestedMessage, nestedCancellationToken);
                    nestedTransactionId = sut.CurrentTransaction.TransactionId;
                }, null, outerCancellationToken);

                hasActiveTransactionAfterNested = sut.HasActiveTransaction;
                ambientTransactionIdAfterNested = context.Database.CurrentTransaction?.TransactionId;

                await context.Set<OutboxMessage>().AddAsync(postNestedMessage, outerCancellationToken);
            }, null);

            nestedTransactionId.Should().Be(outerTransactionId, "the nested unit of work adopts the transaction the outer one began");
            outerTransactionId.Should().NotBe(Guid.Empty);

            // INVARIANT: RelationalTransaction.CommitAsync clears the context's current transaction, so a nested
            // commit and a nested dispose both leave this pair null - one pair discriminates both failure modes.
            hasActiveTransactionAfterNested.Should().BeTrue("a nested unit of work must not close the transaction the outer one began");
            ambientTransactionIdAfterNested.Should().Be(outerTransactionId);

            sut.HasActiveTransaction.Should().BeFalse("the outer unit of work began the transaction, so it commits and disposes it");

            using var freshContext = harness.CreateContext();
            var persistedMessageIds = await freshContext.Set<OutboxMessage>()
                .Where(m => m.MessageId == outerMessage.MessageId
                    || m.MessageId == nestedMessage.MessageId
                    || m.MessageId == postNestedMessage.MessageId)
                .Select(m => m.MessageId)
                .ToListAsync();

            persistedMessageIds.Should().BeEquivalentTo(
                new[] { outerMessage.MessageId, nestedMessage.MessageId, postNestedMessage.MessageId },
                "the transaction stayed usable after the nested call and the outer commit is the single commit point for every write");
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
