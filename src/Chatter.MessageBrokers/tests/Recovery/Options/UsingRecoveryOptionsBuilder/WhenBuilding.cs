using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        public void MustHonourConfiguredMaxRetryAttemptsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts"] = "42"
                })
                .Build();

            var options = RecoveryOptionsBuilder.FromConfig(services, configuration);

            options.Should().NotBeNull();
            options.MaxRetryAttempts.Should().Be(42);
            options.CircuitBreakerOptions.Should().NotBeNull();
        }

        [Fact]
        public void MustHonourNestedCircuitBreakerSectionWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen"] = "9"
                })
                .Build();

            var options = RecoveryOptionsBuilder.FromConfig(services, configuration);

            options.CircuitBreakerOptions.NumberOfFailuresBeforeOpen.Should().Be(9);
        }

        [Fact]
        public void MustThrowNamingInvalidCircuitBreakerOptionWhenNestedSectionCarriesInvalidValue()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:ConcurrentHalfOpenAttempts"] = "0"
                })
                .Build();

            var fromConfig = () => RecoveryOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<CircuitBreakerOptionsValidationException>()
                .WithMessage($"*{nameof(CircuitBreakerOptions.ConcurrentHalfOpenAttempts)}*");
        }

        [Fact]
        public void MustRetainDefaultMaxRetryAttemptsWhenFromConfigSectionOmitsIt()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen"] = "9"
                })
                .Build();

            var options = RecoveryOptionsBuilder.FromConfig(services, configuration);

            options.MaxRetryAttempts.Should().Be(5);
        }

        [Fact]
        public void MustRetainEveryFluentDefaultWhenFromConfigSectionPresentWithoutChildKeys()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [RecoveryOptionsBuilder.RecoveryOptionsSectionName] = string.Empty
                })
                .Build();
            configuration.GetSection(RecoveryOptionsBuilder.RecoveryOptionsSectionName).Exists().Should().BeTrue();

            var options = RecoveryOptionsBuilder.FromConfig(services, configuration);

            options.MaxRetryAttempts.Should().Be(5);
            options.CircuitBreakerOptions.OpenToHalfOpenWaitTimeInSeconds.Should().Be(15);
            options.CircuitBreakerOptions.ConcurrentHalfOpenAttempts.Should().Be(1);
            options.CircuitBreakerOptions.NumberOfFailuresBeforeOpen.Should().Be(5);
            options.CircuitBreakerOptions.NumberOfHalfOpenSuccessesToClose.Should().Be(3);
            options.CircuitBreakerOptions.SecondsOpenBeforeCriticalFailureNotification.Should().Be(1800);
        }

        [Fact]
        public void MustLeaveNestedCircuitBreakerOptionsResolvableWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen"] = "9"
                })
                .Build();

            var options = RecoveryOptionsBuilder.FromConfig(services, configuration);

            services.BuildServiceProvider().GetRequiredService<CircuitBreakerOptions>()
                .Should().BeSameAs(options.CircuitBreakerOptions);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts", "42"));

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<RecoveryOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<RecoveryOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsSnapshotWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts", "42"));

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsSnapshot<RecoveryOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<RecoveryOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsMonitorWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts", "42"));

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsMonitor<RecoveryOptions>>().CurrentValue
                .Should().BeSameAs(provider.GetRequiredService<RecoveryOptions>());
        }

        [Fact]
        public void MustResolveNonNullNestedCircuitBreakerOptionsFromTheOptionsFacetWhenFromConfigSectionOmitsThem()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts", "42"));

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<RecoveryOptions>>().Value.CircuitBreakerOptions
                .Should().NotBeNull();
        }

        [Fact]
        public void MustResolveOneNestedCircuitBreakerOptionsThroughEveryEntryPointWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen", "9"));

            using var provider = services.BuildServiceProvider();
            var nestedCircuitBreakerOptions = provider.GetRequiredService<IOptions<RecoveryOptions>>().Value.CircuitBreakerOptions;

            nestedCircuitBreakerOptions.Should().BeSameAs(provider.GetRequiredService<IOptions<CircuitBreakerOptions>>().Value);
            nestedCircuitBreakerOptions.Should().BeSameAs(provider.GetRequiredService<CircuitBreakerOptions>());
            nestedCircuitBreakerOptions.Should().BeSameAs(provider.GetRequiredService<RecoveryOptions>().CircuitBreakerOptions);
        }

        [Fact]
        public void MustRetainDefaultMaxRetryAttemptsOnTheOptionsFacetWhenFromConfigSectionOmitsIt()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen", "9"));

            using var provider = services.BuildServiceProvider();
            var facetOptions = provider.GetRequiredService<IOptions<RecoveryOptions>>().Value;

            facetOptions.MaxRetryAttempts.Should().Be(5);
            facetOptions.CircuitBreakerOptions.NumberOfFailuresBeforeOpen.Should().Be(9);
        }

        [Fact]
        public void MustRegisterNoOptionsFacetWhenNestedCircuitBreakerValueIsInvalid()
        {
            var services = new ServiceCollection();

            var fromConfig = () => RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:ConcurrentHalfOpenAttempts", "0"));

            fromConfig.Should().Throw<CircuitBreakerOptionsValidationException>();
            services.Any(DescribesRecoveryOptions).Should().BeFalse();

            using var provider = services.BuildServiceProvider();

            provider.GetService<IOptions<RecoveryOptions>>().Should().BeNull();
        }

        private static IConfiguration BuildConfigurationWith(string configurationKey, string value)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [configurationKey] = value
                })
                .Build();

        private static bool DescribesRecoveryOptions(ServiceDescriptor descriptor)
            => descriptor.ServiceType == typeof(RecoveryOptions)
                || descriptor.ServiceType == typeof(IOptions<RecoveryOptions>)
                || descriptor.ServiceType == typeof(IOptionsSnapshot<RecoveryOptions>)
                || descriptor.ServiceType == typeof(IOptionsMonitor<RecoveryOptions>)
                || descriptor.ServiceType == typeof(IConfigureOptions<RecoveryOptions>);
    }
}
