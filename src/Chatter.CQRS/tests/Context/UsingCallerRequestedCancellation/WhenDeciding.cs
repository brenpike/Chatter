using Chatter.CQRS.Context;
using FluentAssertions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Context.UsingCallerRequestedCancellation
{
    public class WhenDeciding
    {
        [Fact]
        public void MustExplainOperationCanceledExceptionWhenMessageHandlerContextTokenIsSignalled()
            => CallerRequestedCancellation.Explains(new OperationCanceledException(), new MessageHandlerContext(SignalledToken()))
                .Should().BeTrue();

        [Fact]
        public void MustExplainOperationCanceledExceptionWhenQueryHandlerContextTokenIsSignalled()
            => CallerRequestedCancellation.Explains(new OperationCanceledException(), new QueryHandlerContext(SignalledToken()))
                .Should().BeTrue();

        [Fact]
        public void MustNotExplainANonCancellationFaultEvenWhenTokenIsSignalled()
            => CallerRequestedCancellation.Explains(new InvalidOperationException(), new MessageHandlerContext(SignalledToken()))
                .Should().BeFalse();

        [Fact]
        public void MustNotExplainOperationCanceledExceptionWhenTokenIsNotSignalled()
            => CallerRequestedCancellation.Explains(new OperationCanceledException(), new MessageHandlerContext(default(CancellationToken)))
                .Should().BeFalse();

        [Fact]
        public void MustNotExplainOperationCanceledExceptionWhenQueryHandlerContextTokenIsNotSignalled()
            => CallerRequestedCancellation.Explains(new OperationCanceledException(), new QueryHandlerContext(default(CancellationToken)))
                .Should().BeFalse();

        [Fact]
        public void MustNotExplainOperationCanceledExceptionWhenMessageHandlerContextIsNull()
            => CallerRequestedCancellation.Explains(new OperationCanceledException(), (IMessageHandlerContext)null)
                .Should().BeFalse();

        [Fact]
        public void MustNotExplainOperationCanceledExceptionWhenQueryHandlerContextIsNull()
            => CallerRequestedCancellation.Explains(new OperationCanceledException(), (IQueryHandlerContext)null)
                .Should().BeFalse();

        [Fact]
        public void MustExplainTaskCanceledExceptionWhenTokenIsSignalled()
            => CallerRequestedCancellation.Explains(new TaskCanceledException(), new MessageHandlerContext(SignalledToken()))
                .Should().BeTrue();

        [Fact]
        public void MustNotExplainANullFaultEvenWhenTokenIsSignalled()
            => CallerRequestedCancellation.Explains(null, new MessageHandlerContext(SignalledToken()))
                .Should().BeFalse();

        [Fact]
        public void MustNotExplainObjectDisposedExceptionEvenWhenTokenIsSignalled()
            => CallerRequestedCancellation.Explains(new ObjectDisposedException("disposed"), new MessageHandlerContext(SignalledToken()))
                .Should().BeFalse();

        private static CancellationToken SignalledToken()
            => new CancellationToken(canceled: true);
    }
}
