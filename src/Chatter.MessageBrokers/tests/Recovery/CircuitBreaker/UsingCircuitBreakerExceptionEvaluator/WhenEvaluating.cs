using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using Moq;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.CircuitBreaker.UsingCircuitBreakerExceptionEvaluator
{
    public class WhenEvaluating : Testing.Core.Context
    {
        private readonly Mock<ICircuitBreakerExceptionPredicatesProvider> _provider
            = new Mock<ICircuitBreakerExceptionPredicatesProvider>();

        private CircuitBreakerExceptionEvaluator SutWith(params Predicate<Exception>[] predicates)
        {
            _provider.Setup(p => p.GetExceptionPredicates()).Returns(predicates);
            return new CircuitBreakerExceptionEvaluator(new[] { _provider.Object });
        }

        [Fact]
        public void MustReturnTrueWhenAPredicateMatchesTheException()
            => SutWith(e => e is FakeRecoverableException)
                .ShouldTrip(new FakeRecoverableException()).Should().BeTrue();

        [Fact]
        public void MustReturnFalseWhenNoPredicateMatchesTheException()
            => SutWith(e => e is FakeRecoverableException)
                .ShouldTrip(new InvalidOperationException()).Should().BeFalse();

        [Fact]
        public void MustReturnFalseWhenProviderSuppliesNoPredicates()
            => SutWith(Array.Empty<Predicate<Exception>>())
                .ShouldTrip(new FakeRecoverableException()).Should().BeFalse();

        [Fact]
        public void MustReturnTrueWhenAnyPredicateMatchesAmongMany()
            => SutWith(e => false, e => e is FakeRecoverableException)
                .ShouldTrip(new FakeRecoverableException()).Should().BeTrue();

        [Fact]
        public void MustTreatNullPredicateInListAsNonMatch()
            => SutWith(null, e => e is FakeRecoverableException)
                .ShouldTrip(new InvalidOperationException()).Should().BeFalse();

        [Fact]
        public void MustReturnFalseWhenConstructedWithNullProviderCollection()
            => new CircuitBreakerExceptionEvaluator(null)
                .ShouldTrip(new FakeRecoverableException()).Should().BeFalse();
    }
}
