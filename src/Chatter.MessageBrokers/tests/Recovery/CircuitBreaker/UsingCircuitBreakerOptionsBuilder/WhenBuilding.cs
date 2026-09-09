using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        public void MustNotRegisterAnyServiceWhenResolved()
        {
            var services = new ServiceCollection();

            var options = CircuitBreakerOptionsBuilder.Create(services)
                .SetNumberOfFailuresBeforeOpen(7)
                .IsTrippedBy<InvalidOperationException>()
                .Resolve();

            options.NumberOfFailuresBeforeOpen.Should().Be(7);
            options.ConcurrentHalfOpenAttempts.Should().Be(1);
            services.Should().BeEmpty();
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

        // This builder used to bind a ConcurrentHalfOpenAttempts of 0 and let the host start, and the acceptance was
        // recorded here as a deferral naming issue #423. #423 closes it: the value is now refused at build time,
        // and the deferral record becomes the pin for the refusal. The bound is derived below from the semaphore
        // the circuit breaker actually constructs rather than restated as a literal.
        [Fact]
        public async Task MustRefuseAConfiguredConcurrentHalfOpenAttemptsOfZero()
        {
            var services = new ServiceCollection();

            (await SemaphoreAdmitsATrial(0)).Should().BeFalse("new SemaphoreSlim(0, 0) is a legal semaphore that admits nothing, so the first half-open trial would block for good");

            var fromConfig = () => CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("ConcurrentHalfOpenAttempts", "0"));

            fromConfig.Should().Throw<ConfiguredValueRefusedException>();
            services.Should().BeEmpty();
        }

        [Fact]
        public async Task MustRefuseAConfiguredNegativeConcurrentHalfOpenAttempts()
        {
            var services = new ServiceCollection();

            (await SemaphoreAdmitsATrial(-1)).Should().BeFalse("new SemaphoreSlim(-1, -1) throws out of the circuit breaker constructor, so the breaker cannot even be resolved");

            var fromConfig = () => CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("ConcurrentHalfOpenAttempts", "-1"));

            fromConfig.Should().Throw<ConfiguredValueRefusedException>();
            services.Should().BeEmpty();
        }

        [Fact]
        public async Task MustAcceptTheSmallestConcurrentHalfOpenAttemptsTheSemaphoreAdmits()
        {
            var services = new ServiceCollection();

            (await SemaphoreAdmitsATrial(1)).Should().BeTrue("one is the smallest count new SemaphoreSlim(n, n) admits a trial with, so it is the bound the builder refuses below");

            var options = CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("ConcurrentHalfOpenAttempts", "1"));

            options.ConcurrentHalfOpenAttempts.Should().Be(1);
        }

        /// <summary>
        /// A zero wait is load-bearing behaviour, not a degenerate value: it is always already elapsed, so an open
        /// circuit never refuses and the first call after opening goes straight to a half-open trial. Every
        /// <c>CircuitBreakerOptionsCreator</c> default depends on it.
        /// </summary>
        [Fact]
        public void MustAcceptAConfiguredOpenToHalfOpenWaitTimeOfZero()
        {
            var services = new ServiceCollection();

            TaskDelayAccepts(TimeSpan.FromSeconds(0)).Should().BeTrue("Task.Delay completes a zero delay immediately");

            var options = CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("OpenToHalfOpenWaitTimeInSeconds", "0"));

            options.OpenToHalfOpenWaitTimeInSeconds.Should().Be(0);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(15)]
        [InlineData(-1)]
        [InlineData(4_294_967)]
        [InlineData(4_294_968)]
        [InlineData(int.MaxValue)]
        public void MustAgreeWithTaskDelayAboutAConfiguredOpenToHalfOpenWaitTime(int timeInSeconds)
        {
            var services = new ServiceCollection();
            // The circuit breaker turns this value into a TimeSpan and hands it to Task.Delay, so Task.Delay is
            // asked here whether it can wait it rather than having its accepted range restated in this test.
            var theSinkCanWaitIt = TaskDelayAccepts(TimeSpan.FromSeconds(timeInSeconds));

            var fromConfig = () => CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("OpenToHalfOpenWaitTimeInSeconds", timeInSeconds.ToString()));

            if (theSinkCanWaitIt)
            {
                fromConfig.Should().NotThrow<ConfiguredValueRefusedException>();
            }
            else
            {
                fromConfig.Should().Throw<ConfiguredValueRefusedException>();
                services.Should().BeEmpty();
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1800)]
        [InlineData(-1)]
        [InlineData(4_294_967)]
        [InlineData(4_294_968)]
        [InlineData(int.MaxValue)]
        public void MustAgreeWithTimerChangeAboutAConfiguredTimeOpenBeforeCriticalEvent(int timeInSeconds)
        {
            var services = new ServiceCollection();
            // The circuit breaker arms its critical-failure notification by handing this value, as a TimeSpan, to
            // Timer.Change, so a real timer is asked here whether it can schedule it.
            var theSinkCanScheduleIt = TimerChangeAccepts(TimeSpan.FromSeconds(timeInSeconds));

            var fromConfig = () => CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("SecondsOpenBeforeCriticalFailureNotification", timeInSeconds.ToString()));

            if (theSinkCanScheduleIt)
            {
                fromConfig.Should().NotThrow<ConfiguredValueRefusedException>();
            }
            else
            {
                fromConfig.Should().Throw<ConfiguredValueRefusedException>();
                services.Should().BeEmpty();
            }
        }

        /// <summary>
        /// No value of these two knobs faults their sink - the state store counts <c>++count &gt;= threshold</c>, so a
        /// zero or a negative means 'trip, or close, on the first' - and an operator is entitled to state that. They
        /// are deliberately left unvalidated, and this pins that decision against a later sweep that would refuse
        /// every non-positive number on sight.
        /// </summary>
        [Theory]
        [InlineData("NumberOfFailuresBeforeOpen")]
        [InlineData("NumberOfHalfOpenSuccessesToClose")]
        public void MustAcceptAConfiguredCountThresholdOfZero(string thresholdKey)
        {
            var services = new ServiceCollection();

            var fromConfig = () => CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith(thresholdKey, "0"));

            fromConfig.Should().NotThrow<ConfiguredValueRefusedException>();
        }

        [Fact]
        public void MustNameTheRefusedPropertyValueBoundAndResolvedSectionPathWhenAConfiguredValueIsRefused()
        {
            var services = new ServiceCollection();

            var fromConfig = () => CircuitBreakerOptionsBuilder.FromConfig(services, BuildConfigurationWith("ConcurrentHalfOpenAttempts", "0"));

            var refusal = fromConfig.Should().Throw<ConfiguredValueRefusedException>().Which;
            refusal.OptionName.Should().Be($"{nameof(CircuitBreakerOptions)}.{nameof(CircuitBreakerOptions.ConcurrentHalfOpenAttempts)}");
            refusal.RefusedValue.Should().Be(0);
            refusal.ConfigurationPath.Should().Be(CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName);
            refusal.Message.Should().Contain(refusal.OptionName)
                   .And.Contain("0")
                   .And.Contain(refusal.ConfigurationPath)
                   .And.Contain(refusal.RequiredBound);
        }

        /// <summary>
        /// A builder retargeted onto a custom section name has a path the documented section constant does not
        /// describe, so the refusal has to name the section the builder actually resolved.
        /// </summary>
        [Fact]
        public void MustNameTheResolvedCustomSectionPathWhenAConfiguredValueIsRefusedThroughACustomSectionName()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CustomSectionName}:ConcurrentHalfOpenAttempts"] = "0"
                })
                .Build();

            var fromConfig = () => CircuitBreakerOptionsBuilder.FromConfig(services, configuration, CustomSectionName);

            fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                      .Which.ConfigurationPath.Should().Be(CustomSectionName);
        }

        private const string CustomSectionName = "Custom:CircuitBreaker";

        // The bound is derived from the semaphore the circuit breaker constructs from this value and then awaits: a
        // count below one either throws out of the constructor or yields a semaphore that admits no trial at all.
        private static async Task<bool> SemaphoreAdmitsATrial(int concurrentHalfOpenAttempts)
        {
            SemaphoreSlim halfOpenSemaphore;
            try
            {
                halfOpenSemaphore = new SemaphoreSlim(concurrentHalfOpenAttempts, concurrentHalfOpenAttempts);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            using (halfOpenSemaphore)
            {
                return await halfOpenSemaphore.WaitAsync(TimeSpan.FromMilliseconds(50));
            }
        }

        // INVARIANT: the already-cancelled token keeps this probe from arming a real timer. Task.Delay validates its
        // argument before it observes the token, and the out-of-range candidates above go red the moment that stops
        // being true, so the sink itself stays the oracle for this bound rather than a range restated here.
        private static bool TaskDelayAccepts(TimeSpan delay)
        {
            try
            {
                _ = Task.Delay(delay, new CancellationToken(true));
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static bool TimerChangeAccepts(TimeSpan dueTime)
        {
            using var notificationTimer = new Timer(_ => { });

            try
            {
                return notificationTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
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
