using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Outbox;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Outbox.UsingOutboxProcessingBehavior
{
    public class WhenHandling : Testing.Core.Context
    {
        private class FakeCommand : ICommand { }

        private const string CurrentTransactionIdKey = "CurrentTransactionId";

        private readonly Mock<IOutboxProcessor> _outboxProcessor = new Mock<IOutboxProcessor>();
        // FakeCommand is private, so Castle cannot proxy ILogger<OutboxProcessingBehavior<FakeCommand>>;
        // a real NullLogger sidesteps the dynamic proxy. The behavior under test never asserts on logging.
        private readonly ILogger<OutboxProcessingBehavior<FakeCommand>> _logger = NullLogger<OutboxProcessingBehavior<FakeCommand>>.Instance;
        private readonly Mock<IMessageHandlerContext> _context = new Mock<IMessageHandlerContext>();
        private readonly ContextContainer _container = new ContextContainer();
        private readonly CancellationToken _cancellationToken = new CancellationTokenSource().Token;
        private readonly OutboxProcessingBehavior<FakeCommand> _sut;

        public WhenHandling()
        {
            _context.SetupGet(c => c.Container).Returns(_container);
            _context.SetupGet(c => c.CancellationToken).Returns(_cancellationToken);
            _sut = new OutboxProcessingBehavior<FakeCommand>(_outboxProcessor.Object, _logger);
        }

        private static CommandHandlerDelegate RecordingNext(List<string> order)
            => () =>
            {
                order.Add("next");
                return Task.CompletedTask;
            };

        private void IncludeTransactionContext(Guid? transactionId)
        {
            var transactionContext = new TransactionContext();
            if (transactionId.HasValue)
            {
                transactionContext.Container.Include<Guid>(CurrentTransactionIdKey, transactionId.Value);
            }
            _container.Include(transactionContext);
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenOutboxProcessorIsNull()
            => FluentActions.Invoking(() => new OutboxProcessingBehavior<FakeCommand>(null, _logger))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenLoggerIsNull()
            => FluentActions.Invoking(() => new OutboxProcessingBehavior<FakeCommand>(_outboxProcessor.Object, null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustInvokeNext()
        {
            var invoked = false;

            await _sut.Handle(new FakeCommand(), _context.Object, () => { invoked = true; return Task.CompletedTask; });

            invoked.Should().BeTrue();
        }

        [Fact]
        public async Task MustProcessBatchWithTransactionIdWhenTransactionContextPresent()
        {
            var transactionId = Guid.NewGuid();
            IncludeTransactionContext(transactionId);

            await _sut.Handle(new FakeCommand(), _context.Object, () => Task.CompletedTask);

            _outboxProcessor.Verify(p => p.ProcessBatch(transactionId, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustProcessBatchWithEmptyGuidWhenTransactionIdMissing()
        {
            IncludeTransactionContext(null);

            await _sut.Handle(new FakeCommand(), _context.Object, () => Task.CompletedTask);

            _outboxProcessor.Verify(p => p.ProcessBatch(Guid.Empty, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustNotProcessBatchWhenTransactionContextAbsent()
        {
            var invoked = false;

            await _sut.Handle(new FakeCommand(), _context.Object, () => { invoked = true; return Task.CompletedTask; });

            invoked.Should().BeTrue();
            _outboxProcessor.Verify(p => p.ProcessBatch(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MustInvokeNextBeforeProcessingBatch()
        {
            IncludeTransactionContext(Guid.NewGuid());
            var order = new List<string>();
            _outboxProcessor.Setup(p => p.ProcessBatch(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                            .Returns(Task.CompletedTask)
                            .Callback(() => order.Add("processBatch"));

            await _sut.Handle(new FakeCommand(), _context.Object, RecordingNext(order));

            order.Should().Equal("next", "processBatch");
        }

        [Fact]
        public async Task MustPropagateCancellationTokenFromContextToProcessBatch()
        {
            IncludeTransactionContext(Guid.NewGuid());

            await _sut.Handle(new FakeCommand(), _context.Object, () => Task.CompletedTask);

            _outboxProcessor.Verify(p => p.ProcessBatch(It.IsAny<Guid>(), _cancellationToken), Times.Once);
        }
    }
}
