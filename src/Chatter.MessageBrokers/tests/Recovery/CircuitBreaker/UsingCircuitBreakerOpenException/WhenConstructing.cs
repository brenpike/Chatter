using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.CircuitBreaker.UsingCircuitBreakerOpenException
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly FakeRecoverableException _last = new FakeRecoverableException("last");
        private readonly CircuitBreakerOpenException _sut;

        public WhenConstructing()
            => _sut = new CircuitBreakerOpenException(_last);

        [Fact]
        public void MustSetFixedMessageText()
            => _sut.Message.Should().Be("Circuit breaker is still in the OPEN state. Action was not executed.");

        [Fact]
        public void MustPreserveLastExceptionAsInnerException()
            => _sut.InnerException.Should().BeSameAs(_last);

        [Fact]
        public void MustAllowNullLastException()
            => new CircuitBreakerOpenException(null).InnerException.Should().BeNull();
    }
}
