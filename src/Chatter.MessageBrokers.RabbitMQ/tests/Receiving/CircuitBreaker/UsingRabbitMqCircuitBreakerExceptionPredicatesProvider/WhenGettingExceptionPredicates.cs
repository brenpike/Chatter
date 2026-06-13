using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.TestSupport;
using FluentAssertions;
using RabbitMQ.Client.Exceptions;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.CircuitBreaker.UsingRabbitMqCircuitBreakerExceptionPredicatesProvider
{
    // Pins RabbitMqCircuitBreakerExceptionPredicatesProvider: the six transient RabbitMQ/transport faults are
    // matched (circuit-breakable) by AT LEAST ONE predicate, a non-transient exception is matched by NONE, and
    // the predicate count is fixed at six. Provider is internal sealed; reachable via InternalsVisibleTo.
    // Mirrors the Retry twin exactly (the two providers share an identical predicate set).
    public class WhenGettingExceptionPredicates : Testing.Core.Context
    {
        private readonly Chatter.MessageBrokers.RabbitMQ.Receiving.CircuitBreaker.RabbitMqCircuitBreakerExceptionPredicatesProvider _sut =
            new Chatter.MessageBrokers.RabbitMQ.Receiving.CircuitBreaker.RabbitMqCircuitBreakerExceptionPredicatesProvider();

        [Fact]
        public void MustYieldSixPredicates()
            => _sut.GetExceptionPredicates().Should().HaveCount(6);

        [Theory]
        [MemberData(nameof(TransientExceptions))]
        public void MustClassifyTransientExceptionAsCircuitBreakable(Exception transient)
            => _sut.GetExceptionPredicates()
                   .Any(predicate => predicate(transient))
                   .Should().BeTrue("a transient RabbitMQ/transport fault must be matched by at least one circuit-breaker predicate");

        [Fact]
        public void MustNotClassifyNonTransientExceptionAsCircuitBreakable()
        {
            var nonTransient = new InvalidOperationException("not a broker fault");

            _sut.GetExceptionPredicates()
                .Select(predicate => predicate(nonTransient))
                .Should().OnlyContain(result => result == false);
        }

        [Fact]
        public void MustReturnFalseFromEveryPredicateForNull()
            => _sut.GetExceptionPredicates()
                   .Select(predicate => predicate(null))
                   .Should().OnlyContain(result => result == false);

        public static TheoryData<Exception> TransientExceptions()
            => new TheoryData<Exception>
            {
                RabbitMqExceptionFactory.BrokerUnreachable(),
                RabbitMqExceptionFactory.AlreadyClosed(),
                new OperationInterruptedException(),
                new ConnectFailureException("connect failed", new Exception()),
                new SocketException(),
                new IOException("transport closed"),
            };
    }
}
