using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.CircuitBreaker.UsingCircuitBreakerOptionsBuilder
{
    public class WhenBuilding : Testing.Core.Context
    {
        [Fact]
        public void MustBuildDefaultOptions()
        {
            var services = new ServiceCollection();

            var options = CircuitBreakerOptionsBuilder.Create(services).Build();

            options.OpenToHalfOpenWaitTimeInSeconds.Should().Be(15);
            options.ConcurrentHalfOpenAttempts.Should().Be(1);
            options.NumberOfFailuresBeforeOpen.Should().Be(5);
            options.NumberOfHalfOpenSuccessesToClose.Should().Be(3);
            options.SecondsOpenBeforeCriticalFailureNotification.Should().Be(1800);
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServicesIsNull()
        {
            var create = () => CircuitBreakerOptionsBuilder.Create(null);

            create.Should().Throw<ArgumentNullException>();
        }
    }
}
