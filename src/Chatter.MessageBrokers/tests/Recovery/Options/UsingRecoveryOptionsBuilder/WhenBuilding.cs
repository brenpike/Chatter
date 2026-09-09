using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
        public void MustNotRegisterAnyServiceWhenResolved()
        {
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services)
                .WithMaxRetryAttempts(9)
                .WithCircuitBreaker(cb => cb.SetNumberOfFailuresBeforeOpen(7))
                .RetryWhen<InvalidOperationException>()
                .UseNoDelayRecovery()
                .UseRouteToErrorQueueRecoveryAction()
                .Resolve();

            options.MaxRetryAttempts.Should().Be(9);
            options.CircuitBreakerOptions.NumberOfFailuresBeforeOpen.Should().Be(7);
            services.Should().BeEmpty();
        }

        [Fact]
        public void MustNotRegisterCircuitBreakerOptionsBeforeBuildWhenWithCircuitBreakerUsed()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.Create(services).WithCircuitBreaker(cb => cb.SetNumberOfFailuresBeforeOpen(7));

            services.Any(d => d.ServiceType == typeof(CircuitBreakerOptions)).Should().BeFalse();
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
        public async Task MustCompleteTheFirstDelayOfTheResolvedStrategyWhenExponentialDelayExceedsCeiling()
        {
            var services = new ServiceCollection();

            RecoveryOptionsBuilder.Create(services)
                .UseExponentialDelayRecovery(30)
                .Build();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var retryDelayStrategy = scope.ServiceProvider.GetRequiredService<IRetryDelayStrategy>();

            var delaying = async () => await retryDelayStrategy.ExecuteAsync(1);

            await delaying.Should().NotThrowAsync<ArgumentOutOfRangeException>();
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

        /// <summary>
        /// <c>RetryStrategy</c> starts at attempt 1 and gives up once <c>attempts &gt;= MaxRetryAttempts</c>, so every
        /// budget at or below 1 buys exactly one attempt. A 0 or a negative therefore states a budget the sink cannot
        /// express, and binding it silently disables retry instead of configuring it.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public async Task MustRefuseAConfiguredMaxRetryAttemptsBelowTheSmallestBudgetTheRetryStrategyCanExpress(int maxRetryAttempts)
        {
            var services = new ServiceCollection();

            (await CountAttemptsAllowedByTheRetryStrategy(maxRetryAttempts))
                .Should().Be(await CountAttemptsAllowedByTheRetryStrategy(1),
                             "a budget of {0} buys the same single attempt as a budget of 1, so the sink cannot tell them apart", maxRetryAttempts);

            var fromConfig = () => RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts", maxRetryAttempts.ToString()));

            fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                      .Which.OptionName.Should().Be($"{nameof(RecoveryOptions)}.{nameof(RecoveryOptions.MaxRetryAttempts)}");
            services.Should().BeEmpty();
        }

        [Fact]
        public async Task MustAcceptTheSmallestMaxRetryAttemptsTheRetryStrategyCanExpress()
        {
            var services = new ServiceCollection();

            var attemptsAtOne = await CountAttemptsAllowedByTheRetryStrategy(1);
            (await CountAttemptsAllowedByTheRetryStrategy(2))
                .Should().BeGreaterThan(attemptsAtOne, "1 is the smallest budget the sink can express, so it is the bound the builder refuses below");

            var options = RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts", "1"));

            options.MaxRetryAttempts.Should().Be(1);
        }

        /// <summary>
        /// The nested circuit breaker builder has no section of its own, so a value configured for it only arrives
        /// through THIS builder's bind. Validating the finalized graph is what reaches it, and nothing may be
        /// published once it is refused.
        /// </summary>
        [Fact]
        public void MustRefuseANestedCircuitBreakerValueAndPublishNothingWhenThisSectionCarriesIt()
        {
            var services = new ServiceCollection();

            var fromConfig = () => RecoveryOptionsBuilder.FromConfig(services, BuildConfigurationWith($"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:ConcurrentHalfOpenAttempts", "0"));

            fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                      .Which.OptionName.Should().Be($"{nameof(CircuitBreakerOptions)}.{nameof(CircuitBreakerOptions.ConcurrentHalfOpenAttempts)}");
            services.Should().BeEmpty();
        }

        /// <summary>
        /// The exponential ceiling clamps the attempt budget the options carry, and the delay strategy has to be
        /// tuned to that same clamped budget. The two differ only in the strategy's internal delay cap - both caps
        /// are far longer than any test could wait for - so the cap is compared against strategies constructed from
        /// the clamped and the raw argument rather than observed by waiting.
        /// </summary>
        [Fact]
        public void MustTuneTheResolvedExponentialDelayStrategyToTheClampedMaxRetryAttempts()
        {
            var services = new ServiceCollection();

            var options = RecoveryOptionsBuilder.Create(services).UseExponentialDelayRecovery(30).Build();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var resolvedDelayCap = MaxDelayOf(scope.ServiceProvider.GetRequiredService<IRetryDelayStrategy>());

            resolvedDelayCap.Should().Be(MaxDelayOf(new ExponentialDelayRetry(options.MaxRetryAttempts)),
                                         "the strategy has to be tuned to the clamped budget the options carry");
            resolvedDelayCap.Should().NotBe(MaxDelayOf(new ExponentialDelayRetry(30)),
                                            "the raw argument the clamp discarded must not reach the strategy");
        }

        // The delay cap lives in a private field; reflect it to compare the resolved strategy against reference
        // strategies without performing any wall-clock delay.
        private static int MaxDelayOf(IRetryDelayStrategy exponentialDelayStrategy)
            => (int)typeof(ExponentialDelayRetry)
                .GetField("_maxDelayInMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(exponentialDelayStrategy);

        // The bound is derived by running the real RetryStrategy over the configured budget and counting how many
        // times it lets the action run, rather than by restating the loop's accounting in this test.
        private async Task<int> CountAttemptsAllowedByTheRetryStrategy(int maxRetryAttempts)
        {
            var recoveryOptions = New.MessageBrokers().Recovery().RecoveryOptions().WithMaxRetryAttempts(maxRetryAttempts);
            var delay = new Mock<IRetryDelayStrategy>();
            delay.Setup(d => d.ExecuteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var evaluator = new Mock<IRetryExceptionEvaluator>();
            evaluator.Setup(e => e.ShouldRetry(It.IsAny<Exception>())).Returns(true);
            var retryStrategy = new RetryStrategy(recoveryOptions,
                                                  New.Common().RecordingLogger<RetryStrategy>().Creation,
                                                  delay.Object,
                                                  evaluator.Object);

            var attempts = 0;
            // Exhausting the budget is the expected outcome of this count, not a discarded failure - the strategy
            // reports the budget it ran out of by throwing.
            await FluentActions.Invoking(async () => await retryStrategy.ExecuteAsync<int>(() =>
            {
                attempts++;
                throw new InvalidOperationException();
            })).Should().ThrowAsync<MaxRetryAttemptsExceededException>();

            return attempts;
        }

        private static IConfiguration BuildConfigurationWith(string configurationKey, string value)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [configurationKey] = value
                })
                .Build();
    }
}
