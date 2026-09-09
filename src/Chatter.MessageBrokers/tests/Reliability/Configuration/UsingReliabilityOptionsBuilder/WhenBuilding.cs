using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Reliability.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Configuration.UsingReliabilityOptionsBuilder
{
    public class WhenBuilding : Testing.Core.Context
    {
        [Fact]
        public void MustBuildDefaultOptions()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).Build();

            options.RouteMessagesToOutbox.Should().BeFalse();
            options.MinutesToLiveInMemory.Should().Be(10);
            options.EnableOutboxPollingProcessor.Should().BeFalse();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
        }

        [Fact]
        public void MustNotRegisterAnyServiceWhenResolved()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithOutboxRouting().Resolve();

            options.RouteMessagesToOutbox.Should().BeTrue();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
            services.Should().BeEmpty();
        }

        /// <summary>
        /// A configured value of the wrong TYPE is the one configuration failure that still happens while
        /// the options are being built, and the one that names the key: <c>ConfigurationBinder</c> cannot
        /// convert it, so it throws out of <c>Build()</c> before any runtime sink sees the value. Recorded
        /// here so the distinction between a conversion failure and the absent semantic validation issue
        /// #423 tracks is pinned rather than only described in prose. The message is asserted only for the
        /// KEY PATH — the framework words the rest differently on net8.0 and net10.0.
        /// </summary>
        [Fact]
        public void MustFailInTheBinderNamingTheKeyWhenAConfiguredValueIsNotConvertible()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:MinutesToLiveInMemory"] = "abc"
                })
                .Build();

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<InvalidOperationException>()
                      .Which.Message.Should().Contain($"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:MinutesToLiveInMemory");
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServicesIsNull()
        {
            var create = () => ReliabilityOptionsBuilder.Create(null);

            create.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustEnableOutboxRoutingWhenWithOutboxRoutingCalled()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithOutboxRouting().Build();

            options.RouteMessagesToOutbox.Should().BeTrue();
        }

        [Fact]
        public void MustReflectWithInMemoryOutboxTimeToLive()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithInMemoryOutboxTimeToLive(99).Build();

            options.MinutesToLiveInMemory.Should().Be(99);
        }

        [Fact]
        public void MustEnableOutboxPollingProcessorWithCustomIntervalWhenWithOutboxPollingProcessorCalled()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithOutboxPollingProcessor(7500).Build();

            options.EnableOutboxPollingProcessor.Should().BeTrue();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(7500);
        }

        [Fact]
        public void MustEnableOutboxPollingProcessorWithDefaultIntervalWhenWithOutboxPollingProcessorCalledWithoutArgument()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithOutboxPollingProcessor().Build();

            options.EnableOutboxPollingProcessor.Should().BeTrue();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
        }

        [Fact]
        public void MustHonourConfiguredValuesAndRetainOmittedFluentDefaultsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:RouteMessagesToOutbox"] = "true",
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:OutboxProcessingIntervalInMilliseconds"] = "1234"
                })
                .Build();

            var options = ReliabilityOptionsBuilder.FromConfig(services, configuration);

            options.Should().NotBeNull();
            options.RouteMessagesToOutbox.Should().BeTrue();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(1234);
            options.MinutesToLiveInMemory.Should().Be(10);
            options.EnableOutboxPollingProcessor.Should().BeFalse();
        }

        [Fact]
        public void MustRetainEveryFluentDefaultWhenFromConfigSectionPresentWithoutChildKeys()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [ReliabilityOptionsBuilder.ReliabilityOptionsSectionName] = string.Empty
                })
                .Build();
            configuration.GetSection(ReliabilityOptionsBuilder.ReliabilityOptionsSectionName).Exists().Should().BeTrue();

            var options = ReliabilityOptionsBuilder.FromConfig(services, configuration);

            options.RouteMessagesToOutbox.Should().BeFalse();
            options.MinutesToLiveInMemory.Should().Be(10);
            options.EnableOutboxPollingProcessor.Should().BeFalse();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithRouteMessagesToOutbox());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<ReliabilityOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<ReliabilityOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsSnapshotWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithRouteMessagesToOutbox());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsSnapshot<ReliabilityOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<ReliabilityOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsMonitorWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithRouteMessagesToOutbox());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsMonitor<ReliabilityOptions>>().CurrentValue
                .Should().BeSameAs(provider.GetRequiredService<ReliabilityOptions>());
        }

        [Fact]
        public void MustRetainOmittedFluentDefaultsOnTheBuiltOptionsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithRouteMessagesToOutbox());

            using var provider = services.BuildServiceProvider();
            var builtOptions = provider.GetRequiredService<IOptions<ReliabilityOptions>>().Value;

            builtOptions.RouteMessagesToOutbox.Should().BeTrue();
            builtOptions.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
            builtOptions.MinutesToLiveInMemory.Should().Be(10);
        }

        /// <summary>
        /// <c>Task.Delay(0)</c> is legal and completes immediately, so how aggressively the outbox is polled is the
        /// operator's call.
        /// </summary>
        [Fact]
        public void MustAllowAZeroOutboxProcessingInterval()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("0");

            var options = ReliabilityOptionsBuilder.FromConfig(services, configuration);

            options.OutboxProcessingIntervalInMilliseconds.Should().Be(0);
        }

        /// <summary>
        /// <c>Task.Delay</c> rejects anything below -1, so an enabled outbox polling processor configured this way
        /// faults the whole background service. This builder used to bind the value and let the host start, and the
        /// acceptance was recorded here as a deferral naming issue #423. #423 closes it: the value is now refused at
        /// build time, and the deferral record becomes the pin for the refusal.
        /// </summary>
        [Fact]
        public void MustRefuseAConfiguredOutboxProcessingIntervalOfNegativeFive()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("-5");

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<ConfiguredValueRefusedException>();
            services.Should().BeEmpty();
        }

        /// <summary>
        /// -1 is <c>Timeout.Infinite</c>, so <c>Task.Delay</c> takes it happily and an ENABLED poller then waits for
        /// good. The polling sink is the only thing that can express 'never poll again', and it cannot, so the value
        /// is refused rather than bound into a processor that would silently stop.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(5000)]
        [InlineData(-1)]
        [InlineData(-5)]
        [InlineData(int.MinValue)]
        public void MustAgreeWithTheOutboxPollingSinkAboutAConfiguredProcessingInterval(int interval)
        {
            var services = new ServiceCollection();
            var theSinkCanPollAtIt = TaskDelayAccepts(interval) && interval != Timeout.Infinite;

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithOutboxProcessingInterval(interval.ToString()));

            if (theSinkCanPollAtIt)
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
        /// The double converter takes "NaN", "Infinity" and "1e300" straight off a configuration section, and NaN
        /// then slips through the outbox's own <c>ttl &lt;= 0</c> disable guard - every comparison against it is
        /// false - to fault the expiry scan. A non-positive ttl stays accepted: that IS the disable branch.
        /// </summary>
        [Theory]
        [InlineData("0")]
        [InlineData("10")]
        [InlineData("-5")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-Infinity")]
        [InlineData("1e300")]
        [InlineData("-1e300")]
        public void MustAgreeWithTheExpiryScanAboutAConfiguredMinutesToLiveInMemory(string minutesToLiveInMemory)
        {
            var services = new ServiceCollection();
            var theSinkCanScheduleIt = ExpiryScanAccepts(double.Parse(minutesToLiveInMemory, CultureInfo.InvariantCulture));

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithMinutesToLiveInMemory(minutesToLiveInMemory));

            if (theSinkCanScheduleIt)
            {
                fromConfig.Should().NotThrow<ConfiguredValueRefusedException>();
            }
            else
            {
                fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                          .Which.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.MinutesToLiveInMemory)}");
                services.Should().BeEmpty();
            }
        }

        /// <summary>
        /// A NaN ttl is the exact value this module measured slipping through the outbox's own disable guard, and
        /// the magnitude offer alone cannot refuse it everywhere: <c>DateTime.AddMinutes</c> rejects a NaN on net8.0
        /// and absorbs it silently on net10.0. Requiring a finite number of minutes is what makes the refusal hold on
        /// every target rather than on one of them.
        /// </summary>
        [Fact]
        public void MustRefuseANonFiniteConfiguredMinutesToLiveInMemoryOnEveryTargetFramework()
        {
            var services = new ServiceCollection();

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithMinutesToLiveInMemory("NaN"));

            fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                      .Which.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.MinutesToLiveInMemory)}");
            services.Should().BeEmpty();
        }

        [Fact]
        public void MustNameTheRefusedPropertyValueBoundAndResolvedSectionPathWhenAConfiguredValueIsRefused()
        {
            var services = new ServiceCollection();

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithOutboxProcessingInterval("-5"));

            var refusal = fromConfig.Should().Throw<ConfiguredValueRefusedException>().Which;
            refusal.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.OutboxProcessingIntervalInMilliseconds)}");
            refusal.RefusedValue.Should().Be(-5);
            refusal.ConfigurationPath.Should().Be(ReliabilityOptionsBuilder.ReliabilityOptionsSectionName);
            refusal.Message.Should().Contain(refusal.OptionName)
                   .And.Contain("-5")
                   .And.Contain(refusal.ConfigurationPath)
                   .And.Contain(refusal.RequiredBound);
        }

        // BrokeredMessageOutboxProcessor awaits Task.Delay(interval) with no try of its own, so Task.Delay is asked
        // here whether it can run the configured interval rather than having its accepted range restated.
        private static bool TaskDelayAccepts(int millisecondsDelay)
        {
            try
            {
                // INVARIANT: the already-cancelled token keeps this probe from arming a real timer. Task.Delay
                // validates its argument before it observes the token, and the out-of-range candidates above go red
                // the moment that stops being true.
                _ = Task.Delay(millisecondsDelay, new CancellationToken(true));
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        // InMemoryBrokeredMessageOutbox adds the ttl to each processed timestamp, so the value is offered to that
        // very call rather than having DateTime.AddMinutes' accepted magnitude restated. AddMinutes is NOT a total
        // oracle though: measured here, net8.0 rejects a NaN and net10.0 absorbs it silently, so a ttl has to be a
        // finite number of minutes before the magnitude question means anything.
        private static bool ExpiryScanAccepts(double minutesToLiveInMemory)
        {
            if (!double.IsFinite(minutesToLiveInMemory))
            {
                return false;
            }

            try
            {
                DateTime.UtcNow.AddMinutes(minutesToLiveInMemory);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static IConfiguration BuildConfigurationWithMinutesToLiveInMemory(string minutesToLiveInMemory)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:MinutesToLiveInMemory"] = minutesToLiveInMemory
                })
                .Build();

        private static IConfiguration BuildConfigurationWithOutboxProcessingInterval(string interval)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:OutboxProcessingIntervalInMilliseconds"] = interval
                })
                .Build();

        private static IConfiguration BuildConfigurationWithRouteMessagesToOutbox()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:RouteMessagesToOutbox"] = "true"
                })
                .Build();
    }
}
