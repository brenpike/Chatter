using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingRetryStrategy
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly RecoveryOptions _options = new RecoveryOptions { MaxRetryAttempts = 3 };
        private readonly ILogger<RetryStrategy> _logger;
        private readonly Mock<IRetryDelayStrategy> _delay = new Mock<IRetryDelayStrategy>();
        private readonly Mock<IRetryExceptionEvaluator> _evaluator = new Mock<IRetryExceptionEvaluator>();

        public WhenConstructing()
            => _logger = New.Common().RecordingLogger<RetryStrategy>().Creation;

        [Fact]
        public void MustThrowArgumentNullExceptionWhenOptionsIsNull()
            => FluentActions.Invoking(() => new RetryStrategy(null, _logger, _delay.Object, _evaluator.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenDelayStrategyIsNull()
            => FluentActions.Invoking(() => new RetryStrategy(_options, _logger, null, _evaluator.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenExceptionEvaluatorIsNull()
            => FluentActions.Invoking(() => new RetryStrategy(_options, _logger, _delay.Object, null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenLoggerIsNull()
            => FluentActions.Invoking(() => new RetryStrategy(_options, null, _delay.Object, _evaluator.Object))
                .Should().Throw<ArgumentNullException>();
    }
}
