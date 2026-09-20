using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    public class WhenExecutingUnitOfWork : Testing.Core.Context
    {
        private DbContextCreator _context;
        private readonly DbContext _dbContext;
        private readonly BrokeredMessageOutbox<DbContext> _outbox;
        private readonly IUnitOfWork _sut;
        private readonly Mock<ILoggerFactory> _loggerFactory;

        public WhenExecutingUnitOfWork()
        {
            _context = New.MessageBrokers().DbContext();
            _dbContext = _context;
            _loggerFactory = new Mock<ILoggerFactory>();
            _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            _outbox = new BrokeredMessageOutbox<DbContext>(_context, _loggerFactory.Object);
            _sut = _outbox;
        }

        [Fact]
        public void MustReportNoActiveTransactionUnderInMemory()
        {
            _sut.HasActiveTransaction.Should().BeFalse();
        }

        [Fact]
        public void MustReportEmptyTransactionIdUnderInMemory()
        {
            _sut.CurrentTransaction.TransactionId.Should().Be(Guid.Empty);
        }

        // INVARIANT: with no transaction on the context there is nothing to commit or roll back, so the handle
        // refuses rather than dereferencing a transaction it never had. The refusal names the condition; a
        // NullReferenceException named only the failure.
        [Fact]
        public async Task MustRefuseCommitWhenNoTransactionActive()
        {
            IPersistanceTransaction transaction = _sut.CurrentTransaction;

            Func<Task> act = () => transaction.CommitAsync();

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustRefuseRollbackWhenNoTransactionActive()
        {
            IPersistanceTransaction transaction = _sut.CurrentTransaction;

            Func<Task> act = () => transaction.RollbackAsync();

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public void MustNotThrowFromDisposeWhenNoTransactionActiveUnderInMemory()
        {
            IPersistanceTransaction transaction = _sut.CurrentTransaction;

            Action act = () => transaction.Dispose();

            act.Should().NotThrow();
        }

        [Fact]
        public async Task MustNotThrowFromDisposeAsyncWhenNoTransactionActiveUnderInMemory()
        {
            IPersistanceTransaction transaction = _sut.CurrentTransaction;

            Func<Task> act = async () => await transaction.DisposeAsync();

            await act.Should().NotThrowAsync();
        }

        // INVARIANT: ExecuteAsync calls DbContext.Database.BeginTransactionAsync, which the
        // EF Core in-memory provider does not support. AS-IS the in-memory provider surfaces
        // TransactionIgnoredWarning as a thrown InvalidOperationException before the supplied
        // operation runs, so ExecuteAsync never completes the unit of work under in-memory.
        [Fact]
        public async Task MustThrowWhenBeginningTransactionUnderInMemory()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();

            Func<Task> act = () => _sut.ExecuteAsync(async ct =>
            {
                await _dbContext.Set<OutboxMessage>().AddAsync(message, ct);
            }, null);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustThrowFromTransactionBeginBeforeRunningOperationThatThrows()
        {
            var operationException = new InvalidOperationException("operation failed");

            Func<Task> act = () => _sut.ExecuteAsync(_ => throw operationException, null);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().NotBeSameAs(operationException);
        }

        [Fact]
        public async Task MustThrowWithNullTransactionContextUnderInMemory()
        {
            Func<Task> act = () => _sut.ExecuteAsync(_ => Task.CompletedTask, null);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        // INVARIANT: a retrying execution strategy is refused outright rather than accommodated. Re-executing a
        // unit of work would silently discard or duplicate a handler's work, so re-execution is made unreachable
        // - the refusal is raised before the strategy is ever handed an operation and before a transaction is begun.
        [Fact]
        public async Task MustRefuseARetryingExecutionStrategy()
        {
            using var context = CreateRetryingContext();
            IUnitOfWork unitOfWork = new BrokeredMessageOutbox<CommitCountingOutboxContext>(context, _loggerFactory.Object);

            Func<Task> act = () => unitOfWork.ExecuteAsync(_ => Task.CompletedTask, null);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("RetriesOnFailure");
        }

        [Fact]
        public async Task MustNotInvokeTheOperationUnderARetryingExecutionStrategy()
        {
            using var context = CreateRetryingContext();
            IUnitOfWork unitOfWork = new BrokeredMessageOutbox<CommitCountingOutboxContext>(context, _loggerFactory.Object);
            var operationInvocationCount = 0;

            Func<Task> act = () => unitOfWork.ExecuteAsync(_ =>
            {
                operationInvocationCount++;
                return Task.CompletedTask;
            }, null);

            await act.Should().ThrowAsync<InvalidOperationException>();
            operationInvocationCount.Should().Be(0);
            context.SaveChangesAsyncCallCount.Should().Be(0);
        }

        [Fact]
        public async Task MustRefuseARetryingExecutionStrategyOnEveryCall()
        {
            using var context = CreateRetryingContext();
            IUnitOfWork unitOfWork = new BrokeredMessageOutbox<CommitCountingOutboxContext>(context, _loggerFactory.Object);

            Func<Task> act = () => unitOfWork.ExecuteAsync(_ => Task.CompletedTask, null);

            await act.Should().ThrowAsync<InvalidOperationException>();
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustNameTheContextStrategyAndBothMigrationsWhenRefusing()
        {
            using var context = CreateRetryingContext();
            IUnitOfWork unitOfWork = new BrokeredMessageOutbox<CommitCountingOutboxContext>(context, _loggerFactory.Object);

            Func<Task> act = () => unitOfWork.ExecuteAsync(_ => Task.CompletedTask, null);

            var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
            message.Should().Contain("UnitOfWork<CommitCountingOutboxContext>");
            message.Should().Contain(nameof(RetryingExecutionStrategy));
            message.Should().Contain("EnableRetryOnFailure");
            message.Should().Contain("WithUnitOfWorkBehavior");
            message.Should().Contain("WithInboxBehavior");
            message.Should().Contain("WithOutboxProcessingBehavior");
        }

        // INVARIANT: TransactionIgnoredWarning is suppressed so the in-memory provider hands back a no-op
        // transaction instead of throwing. Without that suppression BeginTransactionAsync throws on its own and
        // every assertion below would pass whether or not the retrying strategy is refused, proving nothing.
        private static CommitCountingOutboxContext CreateRetryingContext()
        {
            var options = new DbContextOptionsBuilder<CommitCountingOutboxContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .ReplaceService<IExecutionStrategyFactory, RetryingExecutionStrategyFactory>()
                .Options;

            return new CommitCountingOutboxContext(options);
        }
    }
}
