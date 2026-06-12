using Chatter.MessageBrokers.Reliability.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        public void MustTakeConfigBranchAndSkipFluentDefaultsWhenFromConfigSectionPopulated()
        {
            // INVARIANT: production binds the populated section via IConfigurationSection.Get<ReliabilityOptions>(),
            // whose internal-set scalar properties stay at their type default; the fluent else-branch (which would
            // otherwise apply MinutesToLiveInMemory of 10 and an interval of 5000) is skipped, so a populated
            // section yields the type defaults (0), not the fluent defaults.
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
            options.MinutesToLiveInMemory.Should().Be(0);
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(0);
        }
    }
}
