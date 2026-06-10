using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    // INVARIANT: These tests exercise the reliability adapter against a real relational provider (SQLite),
    // unlike WhenExecutingUnitOfWork.cs which documents the InMemory provider's transaction-rejecting behavior.
    public class WhenExecutingUnitOfWorkOverSqlite : Testing.Core.Context, IAsyncDisposable
    {
        private readonly SqliteOutboxContextHarness _harness;
        private readonly SqliteOutboxContext _context;
        private readonly Mock<ILoggerFactory> _loggerFactory;
        private readonly IUnitOfWork _sut;

        public WhenExecutingUnitOfWorkOverSqlite()
        {
            _harness = SqliteOutboxContextHarness.Create();
            _context = _harness.CreateContext();
            _loggerFactory = new Mock<ILoggerFactory>();
            _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            _sut = new BrokeredMessageOutbox<SqliteOutboxContext>(_context, _loggerFactory.Object);
        }

        [Fact]
        public async Task MustPersistOutboxMessageWhenOperationCommits()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            await _sut.ExecuteAsync(async ct =>
            {
                await _context.Set<OutboxMessage>().AddAsync(message, ct);
            }, null);

            _sut.HasActiveTransaction.Should().BeFalse();

            using var freshContext = _harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().NotBeNull();
        }

        [Fact]
        public async Task MustDiscardOutboxMessageWhenOperationThrows()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => _sut.ExecuteAsync(async ct =>
            {
                await _context.Set<OutboxMessage>().AddAsync(message, ct);
                throw operationException;
            }, null);

            await act.Should().ThrowAsync<InvalidOperationException>();

            _sut.HasActiveTransaction.Should().BeFalse();

            using var freshContext = _harness.CreateContext();
            var persisted = await freshContext.Set<OutboxMessage>()
                .SingleOrDefaultAsync(m => m.MessageId == message.MessageId);
            persisted.Should().BeNull();
        }

        // INVARIANT: under SQLite, BeginTransactionAsync succeeds and the default NonRetryingExecutionStrategy
        // is used, so the operation's own exception propagates unchanged (same instance) - in contrast with the
        // InMemory provider, which throws from BeginTransactionAsync before the operation runs.
        [Fact]
        public async Task MustRethrowSameOperationExceptionInstanceWhenOperationThrows()
        {
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => _sut.ExecuteAsync(_ => throw operationException, null);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().BeSameAs(operationException);
        }

        // INVARIANT: when a transaction is already open on the context, BeginAsync returns the existing
        // CurrentTransaction rather than starting a second one. The operation therefore observes the
        // pre-existing TransactionId. ExecuteAsync still commits and disposes that transaction on success,
        // so HasActiveTransaction is false once it returns - the reuse evidence is the id seen inside the operation.
        [Fact]
        public async Task MustReuseExistingTransactionWhenOneIsAlreadyOpen()
        {
            await using var existingTransaction = await _context.Database.BeginTransactionAsync();
            var existingTransactionId = existingTransaction.TransactionId;

            Guid observedTransactionId = Guid.Empty;

            await _sut.ExecuteAsync(_ =>
            {
                observedTransactionId = _sut.CurrentTransaction.TransactionId;
                return Task.CompletedTask;
            }, null);

            observedTransactionId.Should().Be(existingTransactionId);
            observedTransactionId.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public async Task MustPopulateTransactionContextContainerWhenContextProvided()
        {
            var transactionContext = new TransactionContext("receiver");

            Guid transactionIdInsideOperation = Guid.Empty;

            await _sut.ExecuteAsync(_ =>
            {
                transactionIdInsideOperation = _sut.CurrentTransaction.TransactionId;
                return Task.CompletedTask;
            }, transactionContext);

            var containedTransaction = transactionContext.Container.GetOrDefault<IPersistanceTransaction>();
            containedTransaction.Should().NotBeNull();

            var containedTransactionId = transactionContext.Container.Get<Guid>("CurrentTransactionId");
            containedTransactionId.Should().Be(transactionIdInsideOperation);
            containedTransactionId.Should().NotBe(Guid.Empty);
        }

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _harness.DisposeAsync();
        }
    }
}
