using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using System;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingDefaultRetryExceptionPredicatesProvider
{
    public class WhenProvidingPredicates : Testing.Core.Context
    {
        private static bool AnyPredicateMatches(Exception exception)
            => new DefaultRetryExceptionPredicatesProvider()
                .GetExceptionPredicates()
                .Any(predicate => predicate(exception));

        [Theory]
        [InlineData("retry")]
        [InlineData("timeout")]
        [InlineData("time out")]
        [InlineData("rerun")]
        [InlineData("internal server error")]
        [InlineData("waiting")]
        [InlineData("wait until")]
        [InlineData("service unavailable")]
        public void MustNotMatchAnExceptionByItsMessageText(string retiredSubstring)
            => AnyPredicateMatches(new FakeRecoverableException($"handler failed while processing '{retiredSubstring}'"))
                .Should().BeFalse();

        [Fact]
        public void MustMatchATransientBrokeredMessageReceiverException()
            => AnyPredicateMatches(new BrokeredMessageReceiverException("receive failed", isTransient: true))
                .Should().BeTrue();

        [Fact]
        public void MustNotMatchANonTransientBrokeredMessageReceiverException()
            => AnyPredicateMatches(new BrokeredMessageReceiverException("receive failed", isTransient: false))
                .Should().BeFalse();

        [Fact]
        public void MustNotImplementTheCircuitBreakerPredicatesProvider()
            => new DefaultRetryExceptionPredicatesProvider()
                .Should().NotBeAssignableTo<MessageBrokers.Recovery.CircuitBreaker.ICircuitBreakerExceptionPredicatesProvider>();
    }
}
