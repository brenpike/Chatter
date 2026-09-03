using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
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

        [Fact]
        public void MustReflectSetOpenToHalfOpenWaitTime()
        {
            var services = new ServiceCollection();

            var options = CircuitBreakerOptionsBuilder.Create(services).SetOpenToHalfOpenWaitTime(99).Build();

            options.OpenToHalfOpenWaitTimeInSeconds.Should().Be(99);
        }

        [Fact]
        public void MustReflectSetConcurrentHalfOpenAttempts()
        {
            var services = new ServiceCollection();

            var options = CircuitBreakerOptionsBuilder.Create(services).SetConcurrentHalfOpenAttempts(4).Build();

            options.ConcurrentHalfOpenAttempts.Should().Be(4);
        }

        [Fact]
        public void MustReflectSetNumberOfFailuresBeforeOpen()
        {
            var services = new ServiceCollection();

            var options = CircuitBreakerOptionsBuilder.Create(services).SetNumberOfFailuresBeforeOpen(11).Build();

            options.NumberOfFailuresBeforeOpen.Should().Be(11);
        }

        [Fact]
        public void MustReflectSetNumberOfHalfOpenSuccessesBeforeClose()
        {
            var services = new ServiceCollection();

            var options = CircuitBreakerOptionsBuilder.Create(services).SetNumberOfHalfOpenSuccessesBeforeClose(7).Build();

            options.NumberOfHalfOpenSuccessesToClose.Should().Be(7);
        }

        [Fact]
        public void MustReflectSetTimeOpenBeforeCriticalEvent()
        {
            var services = new ServiceCollection();

            var options = CircuitBreakerOptionsBuilder.Create(services).SetTimeOpenBeforeCriticalEvent(60).Build();

            options.SecondsOpenBeforeCriticalFailureNotification.Should().Be(60);
        }

        [Fact]
        public void MustNotThrowWhenIsTrippedByPredicatesIsNull()
        {
            var services = new ServiceCollection();

            var build = () => CircuitBreakerOptionsBuilder.Create(services).IsTrippedBy(null).Build();

            build.Should().NotThrow();
        }

        [Fact]
        public void MustRegisterCircuitBreakerExceptionPredicatesProviderWhenIsTrippedByPredicatesProvided()
        {
            var services = new ServiceCollection();

            CircuitBreakerOptionsBuilder.Create(services)
                .IsTrippedBy(e => e is InvalidOperationException)
                .Build();

            services.Any(d => d.ServiceType == typeof(ICircuitBreakerExceptionPredicatesProvider)).Should().BeTrue();
        }

        [Fact]
        public void MustRegisterCircuitBreakerExceptionPredicatesProviderWhenGenericIsTrippedByUsed()
        {
            var services = new ServiceCollection();

            CircuitBreakerOptionsBuilder.Create(services)
                .IsTrippedBy<InvalidOperationException>()
                .Build();

            services.Any(d => d.ServiceType == typeof(ICircuitBreakerExceptionPredicatesProvider)).Should().BeTrue();
        }

        [Fact]
        public void MustHonourConfiguredValueAndRetainFluentDefaultsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen"] = "42"
                })
                .Build();

            var options = CircuitBreakerOptionsBuilder.FromConfig(services, configuration);

            options.Should().NotBeNull();
            options.NumberOfFailuresBeforeOpen.Should().Be(42);
            options.OpenToHalfOpenWaitTimeInSeconds.Should().Be(15);
            options.ConcurrentHalfOpenAttempts.Should().Be(1);
            options.NumberOfHalfOpenSuccessesToClose.Should().Be(3);
            options.SecondsOpenBeforeCriticalFailureNotification.Should().Be(1800);
        }

        [Fact]
        public void MustRetainEveryFluentDefaultWhenFromConfigSectionPresentWithoutChildKeys()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName] = string.Empty
                })
                .Build();
            configuration.GetSection(CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName).Exists().Should().BeTrue();

            var options = CircuitBreakerOptionsBuilder.FromConfig(services, configuration);

            options.OpenToHalfOpenWaitTimeInSeconds.Should().Be(15);
            options.ConcurrentHalfOpenAttempts.Should().Be(1);
            options.NumberOfFailuresBeforeOpen.Should().Be(5);
            options.NumberOfHalfOpenSuccessesToClose.Should().Be(3);
            options.SecondsOpenBeforeCriticalFailureNotification.Should().Be(1800);
        }

        [Fact]
        public void MustThrowNamingConcurrentHalfOpenAttemptsWhenConfiguredBelowOne()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:ConcurrentHalfOpenAttempts"] = "0"
                })
                .Build();

            var fromConfig = () => CircuitBreakerOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<CircuitBreakerOptionsValidationException>()
                .WithMessage($"*{nameof(CircuitBreakerOptions.ConcurrentHalfOpenAttempts)}*");
        }

        [Fact]
        public void MustNameEveryInvalidValueInOneFailureWhenSeveralConfiguredValuesAreInvalid()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:ConcurrentHalfOpenAttempts"] = "0",
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen"] = "0",
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfHalfOpenSuccessesToClose"] = "-1",
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:OpenToHalfOpenWaitTimeInSeconds"] = "-1",
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:SecondsOpenBeforeCriticalFailureNotification"] = "-1"
                })
                .Build();

            var fromConfig = () => CircuitBreakerOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<CircuitBreakerOptionsValidationException>()
                .WithMessage($"*{nameof(CircuitBreakerOptions.ConcurrentHalfOpenAttempts)}*")
                .WithMessage($"*{nameof(CircuitBreakerOptions.NumberOfFailuresBeforeOpen)}*")
                .WithMessage($"*{nameof(CircuitBreakerOptions.NumberOfHalfOpenSuccessesToClose)}*")
                .WithMessage($"*{nameof(CircuitBreakerOptions.OpenToHalfOpenWaitTimeInSeconds)}*")
                .WithMessage($"*{nameof(CircuitBreakerOptions.SecondsOpenBeforeCriticalFailureNotification)}*");
        }
    }
}
