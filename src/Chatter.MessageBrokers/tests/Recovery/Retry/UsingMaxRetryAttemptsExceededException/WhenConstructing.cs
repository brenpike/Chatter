using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingMaxRetryAttemptsExceededException
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly FakeRecoverableException _inner = new FakeRecoverableException("inner");
        private readonly MaxRetryAttemptsExceededException _sut;

        public WhenConstructing()
            => _sut = new MaxRetryAttemptsExceededException(_inner, 7);

        [Fact]
        public void MustSetFixedMessageText()
            => _sut.Message.Should().Be("The maximum number of retries was exceeded.");

        [Fact]
        public void MustPreserveInnerException()
            => _sut.InnerException.Should().BeSameAs(_inner);

        [Fact]
        public void MustPreserveAttempts()
            => _sut.Attempts.Should().Be(7);
    }
}
