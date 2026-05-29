using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingRetryExceptionEvaluator
{
    public class WhenEvaluating : Testing.Core.Context
    {
        private readonly Mock<IRetryExceptionPredicatesProvider> _provider = new Mock<IRetryExceptionPredicatesProvider>();

        private RetryExceptionEvaluator SutWith(params Predicate<Exception>[] predicates)
        {
            _provider.Setup(p => p.GetExceptionPredicates()).Returns(predicates);
            return new RetryExceptionEvaluator(new[] { _provider.Object });
        }

        [Fact]
        public void MustReturnTrueWhenAPredicateMatchesTheException()
            => SutWith(e => e is FakeRecoverableException)
                .ShouldRetry(new FakeRecoverableException()).Should().BeTrue();

        [Fact]
        public void MustReturnFalseWhenNoPredicateMatchesTheException()
            => SutWith(e => e is FakeRecoverableException)
                .ShouldRetry(new InvalidOperationException()).Should().BeFalse();

        [Fact]
        public void MustReturnFalseWhenProviderSuppliesNoPredicates()
            => SutWith(Array.Empty<Predicate<Exception>>())
                .ShouldRetry(new FakeRecoverableException()).Should().BeFalse();

        [Fact]
        public void MustReturnTrueWhenAnyPredicateMatchesAmongMany()
            => SutWith(e => false, e => e is FakeRecoverableException, e => false)
                .ShouldRetry(new FakeRecoverableException()).Should().BeTrue();

        [Fact]
        public void MustTreatNullPredicateInListAsNonMatch()
            => SutWith(null, e => e is FakeRecoverableException)
                .ShouldRetry(new InvalidOperationException()).Should().BeFalse();

        [Fact]
        public void MustReturnFalseWhenConstructedWithNullProviderCollection()
            => new RetryExceptionEvaluator(null)
                .ShouldRetry(new FakeRecoverableException()).Should().BeFalse();
    }
}
