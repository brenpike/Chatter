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
        public void MustNotReflectWithMaxRetryAttemptsOnNonConfigPath()
        {
            // CHARACTERIZATION: on the non-config Build() path the builder overwrites MaxRetryAttempts
            // with the default (5), discarding any value supplied via WithMaxRetryAttempts. Pinned as-is.
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services).WithMaxRetryAttempts(99).Build();

            options.MaxRetryAttempts.Should().Be(5);
        }
    }
}
