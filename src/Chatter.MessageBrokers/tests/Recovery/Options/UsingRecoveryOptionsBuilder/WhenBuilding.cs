using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
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

        [Fact]
        public void MustBuildNonNullCircuitBreakerOptionsWhenWithCircuitBreakerActionProvided()
        {
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services)
                .WithCircuitBreaker(cb => cb.SetNumberOfFailuresBeforeOpen(7))
                .Build();

            options.CircuitBreakerOptions.Should().NotBeNull();
            options.CircuitBreakerOptions.NumberOfFailuresBeforeOpen.Should().Be(7);
        }

        [Fact]
        public void MustBuildNonNullCircuitBreakerOptionsWhenWithCircuitBreakerActionIsNull()
        {
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services)
                .WithCircuitBreaker(null)
                .Build();

            options.CircuitBreakerOptions.Should().NotBeNull();
        }

        [Fact]
        public void MustClampMaxRetryAttemptsToFifteenWhenExponentialDelayExceedsCeiling()
        {
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services)
                .UseExponentialDelayRecovery(99)
                .Build();

            options.MaxRetryAttempts.Should().Be(15);
        }

        [Fact]
        public void MustNotClampMaxRetryAttemptsWhenExponentialDelayBelowCeiling()
        {
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services)
                .UseExponentialDelayRecovery(3)
                .Build();

            options.MaxRetryAttempts.Should().Be(3);
        }

        [Fact]
        public void MustNotThrowWhenRetryWhenPredicatesIsNull()
        {
            var services = new ServiceCollection();

            var build = () => RecoveryOptionsBuilder.Create(services).RetryWhen(null).Build();

            build.Should().NotThrow();
        }

        [Fact]
        public void MustRegisterRetryExceptionPredicatesProviderWhenRetryWhenPredicatesProvided()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.Create(services)
                .RetryWhen(e => e is InvalidOperationException)
                .Build();

            services.Any(d => d.ServiceType == typeof(IRetryExceptionPredicatesProvider)).Should().BeTrue();
        }

        [Fact]
        public void MustRegisterRetryExceptionPredicatesProviderWhenGenericRetryWhenUsed()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.Create(services)
                .RetryWhen<InvalidOperationException>()
                .Build();

            services.Any(d => d.ServiceType == typeof(IRetryExceptionPredicatesProvider)).Should().BeTrue();
        }

        [Fact]
        public void MustTakeConfigBranchAndSkipFluentDefaultsWhenFromConfigSectionPopulated()
        {
            // INVARIANT: production binds the populated section via IConfigurationSection.Get<RecoveryOptions>(),
            // whose internal-set scalar properties stay at their type default; the fluent else-branch (which would
            // otherwise apply the _defaultMaxRetryAttempts of 5) is skipped, so a populated section yields 0, not 5.
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts"] = "42"
                })
                .Build();

            var options = RecoveryOptionsBuilder.FromConfig(services, configuration);

            options.Should().NotBeNull();
            options.MaxRetryAttempts.Should().Be(0);
            options.CircuitBreakerOptions.Should().NotBeNull();
        }
    }
}
