using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.TestSupport;
using FluentAssertions;
using RabbitMQ.Client.Exceptions;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.Retry.UsingRabbitMqRetryExceptionPredicatesProvider
{
    // Pins RabbitMqRetryExceptionPredicatesProvider: the six transient RabbitMQ/transport faults are matched
    // (retryable) by AT LEAST ONE predicate, a non-transient exception is matched by NONE, and the predicate
    // count is fixed at six. The provider is internal sealed; reachable via InternalsVisibleTo. Mirrors the
    // CircuitBreaker twin exactly (the two providers share an identical predicate set).
    public class WhenGettingExceptionPredicates : Testing.Core.Context
    {
        private readonly Chatter.MessageBrokers.RabbitMQ.Receiving.Retry.RabbitMqRetryExceptionPredicatesProvider _sut =
            new Chatter.MessageBrokers.RabbitMQ.Receiving.Retry.RabbitMqRetryExceptionPredicatesProvider();

        [Fact]
        public void MustYieldSixPredicates()
            => _sut.GetExceptionPredicates().Should().HaveCount(6);

        [Theory]
        [MemberData(nameof(TransientExceptions))]
        public void MustClassifyTransientExceptionAsRetryable(Exception transient)
            => _sut.GetExceptionPredicates()
                   .Any(predicate => predicate(transient))
                   .Should().BeTrue("a transient RabbitMQ/transport fault must be matched by at least one retry predicate");

        [Fact]
        public void MustNotClassifyNonTransientExceptionAsRetryable()
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
