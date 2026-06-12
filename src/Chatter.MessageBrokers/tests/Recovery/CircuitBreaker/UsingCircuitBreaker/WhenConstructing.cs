using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.CircuitBreaker.UsingCircuitBreaker
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly Mock<ICircuitBreakerStateStore> _store = new Mock<ICircuitBreakerStateStore>();
        private readonly CircuitBreakerOptions _options;
        private readonly ILogger<MessageBrokers.Recovery.CircuitBreaker.CircuitBreaker> _logger;
        private readonly Mock<ICircuitBreakerExceptionEvaluator> _evaluator = new Mock<ICircuitBreakerExceptionEvaluator>();

        public WhenConstructing()
        {
            _options = New.MessageBrokers().Recovery().CircuitBreakerOptions();
            _logger = New.Common().Logger<MessageBrokers.Recovery.CircuitBreaker.CircuitBreaker>().Creation;
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenOptionsIsNull()
            => FluentActions.Invoking(() => new MessageBrokers.Recovery.CircuitBreaker.CircuitBreaker(_store.Object, null, _logger, _evaluator.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenStateStoreIsNull()
            => FluentActions.Invoking(() => new MessageBrokers.Recovery.CircuitBreaker.CircuitBreaker(null, _options, _logger, _evaluator.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenLoggerIsNull()
            => FluentActions.Invoking(() => new MessageBrokers.Recovery.CircuitBreaker.CircuitBreaker(_store.Object, _options, null, _evaluator.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenExceptionEvaluatorIsNull()
            => FluentActions.Invoking(() => new MessageBrokers.Recovery.CircuitBreaker.CircuitBreaker(_store.Object, _options, _logger, null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustNotThrowWhenDisposedTwice()
        {
            var sut = new MessageBrokers.Recovery.CircuitBreaker.CircuitBreaker(_store.Object, _options, _logger, _evaluator.Object);
            sut.Dispose();

            // The _disposedValue guard must make a second Dispose a no-op rather than re-disposing the (already-null) timer/semaphore.
            FluentActions.Invoking(() => sut.Dispose()).Should().NotThrow();
        }
    }
}
