using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

        // A ConcurrentHalfOpenAttempts of 0 becomes new SemaphoreSlim(0, 0), which the circuit breaker cannot run
        // with, and this builder binds it anyway. Build-time validation of configured options is tracked by
        // issue #423, so the acceptance is recorded here rather than overlooked.
        [Fact]
        public void MustAcceptAConfiguredConcurrentHalfOpenAttemptsOfZero()
        {
            var services = new ServiceCollection();

            var options = CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("ConcurrentHalfOpenAttempts", "0"));

            options.ConcurrentHalfOpenAttempts.Should().Be(0);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("NumberOfFailuresBeforeOpen", "42"));

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<CircuitBreakerOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<CircuitBreakerOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsSnapshotWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("NumberOfFailuresBeforeOpen", "42"));

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsSnapshot<CircuitBreakerOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<CircuitBreakerOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsMonitorWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("NumberOfFailuresBeforeOpen", "42"));

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsMonitor<CircuitBreakerOptions>>().CurrentValue
                .Should().BeSameAs(provider.GetRequiredService<CircuitBreakerOptions>());
        }

        [Fact]
        public void MustRetainFluentDefaultsOnTheOptionsFacetWhenFromConfigSectionOmitsKeys()
        {
            var services = new ServiceCollection();

            CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("NumberOfFailuresBeforeOpen", "42"));

            using var provider = services.BuildServiceProvider();
            var facetOptions = provider.GetRequiredService<IOptions<CircuitBreakerOptions>>().Value;

            facetOptions.NumberOfFailuresBeforeOpen.Should().Be(42);
            facetOptions.OpenToHalfOpenWaitTimeInSeconds.Should().Be(15);
            facetOptions.ConcurrentHalfOpenAttempts.Should().Be(1);
            facetOptions.NumberOfHalfOpenSuccessesToClose.Should().Be(3);
            facetOptions.SecondsOpenBeforeCriticalFailureNotification.Should().Be(1800);
        }

        private static IConfiguration BuildConfigurationWith(string key, string value)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:{key}"] = value
                })
                .Build();
    }
}
