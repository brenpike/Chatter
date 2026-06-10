using Chatter.MessageBrokers.Recovery.Options;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Options.UsingRecoveryOptionsBuilder
{
    public class WhenBuilding : Testing.Core.Context
    {
        [Fact]
        public void MustDefaultMaxRetryAttemptsToFive()
        {
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services).Build();

            options.MaxRetryAttempts.Should().Be(5);
        }

        [Fact]
        public void MustBuildNonNullCircuitBreakerOptions()
        {
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services).Build();

            options.CircuitBreakerOptions.Should().NotBeNull();
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServicesIsNull()
        {
            var create = () => RecoveryOptionsBuilder.Create(null);

            create.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustReflectWithMaxRetryAttemptsOnNonConfigPath()
        {
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services).WithMaxRetryAttempts(99).Build();

            options.MaxRetryAttempts.Should().Be(99);
        }
    }
}
