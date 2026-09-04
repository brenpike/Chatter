using Chatter.MessageBrokers.Reliability.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
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

        [Fact]
        public void MustThrowNamingTheOutboxProcessingIntervalWhenConfiguredBelowNegativeOne()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("-2");

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<ReliabilityOptionsValidationException>()
                .WithMessage($"*{nameof(ReliabilityOptions.OutboxProcessingIntervalInMilliseconds)}*-2*");
        }

        /// <summary>
        /// -1 is <c>Timeout.Infinite</c>, so <c>Task.Delay</c> accepts it and the outbox polling processor never
        /// faults - it simply waits forever after its first pass. Disabling the processor has to go through
        /// <see cref="ReliabilityOptions.EnableOutboxPollingProcessor"/>, never through an interval that disables it
        /// by inference, so the violation has to explain that rather than merely cite the delay primitive.
        /// </summary>
        [Fact]
        public void MustExplainTheSilentDisableWhenConfiguredIntervalIsNegativeOne()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("-1");

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<ReliabilityOptionsValidationException>()
                .Which.Violations.Should().ContainSingle()
                .Which.Should().Contain(nameof(ReliabilityOptions.EnableOutboxPollingProcessor));
        }

        /// <summary>
        /// <c>Task.Delay(0)</c> is legal and completes immediately, so how aggressively the outbox is polled is the
        /// operator's call, not a validation concern.
        /// </summary>
        [Fact]
        public void MustAllowAZeroOutboxProcessingInterval()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("0");

            var options = ReliabilityOptionsBuilder.FromConfig(services, configuration);

            options.OutboxProcessingIntervalInMilliseconds.Should().Be(0);
        }

        [Fact]
        public void MustThrowWhenTheFluentOutboxPollingProcessorIntervalIsNegative()
        {
            var services = new ServiceCollection();

            var build = () => ReliabilityOptionsBuilder.Create(services).WithOutboxPollingProcessor(-5).Build();

            build.Should().Throw<ReliabilityOptionsValidationException>()
                .WithMessage($"*{nameof(ReliabilityOptions.OutboxProcessingIntervalInMilliseconds)}*-5*");
        }

        /// <summary>
        /// Deliberate, not a bug: the FINALIZED instance is validated, never its reachability. An interval only the
        /// disabled processor would have read is still rejected, because the deployment that turns
        /// <see cref="ReliabilityOptions.EnableOutboxPollingProcessor"/> on is not the place to discover it.
        /// </summary>
        [Fact]
        public void MustThrowWhenTheConfiguredIntervalIsInvalidEvenThoughTheOutboxPollingProcessorIsDisabled()
        {
            ReliabilityOptionsBuilder.FromConfig(new ServiceCollection(), BuildConfigurationWithOutboxProcessingInterval("5000"))
                .EnableOutboxPollingProcessor.Should().BeFalse();

            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("-2");

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<ReliabilityOptionsValidationException>();
        }

        /// <summary>
        /// Aggregation is one message naming every offending value, so an operator does not pay a deployment per
        /// invalid option. Only one reliability option is validated today, so the aggregation is pinned on the
        /// exception's own contract rather than through a configuration that cannot yet carry two violations.
        /// </summary>
        [Fact]
        public void MustNameEveryViolationInOneMessage()
        {
            var violations = new[] { "first violation", "second violation" };

            var exception = new ReliabilityOptionsValidationException(violations);

            exception.Violations.Should().Equal(violations);
            exception.Message.Should().Contain("first violation").And.Contain("second violation");
        }

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
