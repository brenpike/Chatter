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

        private static IConfiguration BuildConfigurationWithRouteMessagesToOutbox()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:RouteMessagesToOutbox"] = "true"
                })
                .Build();
    }
}
