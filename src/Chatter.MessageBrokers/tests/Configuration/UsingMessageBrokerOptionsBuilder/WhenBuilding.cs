using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Configuration.UsingMessageBrokerOptionsBuilder
{
    public class WhenBuilding : Testing.Core.Context
    {
        [Fact]
        public void MustDefaultTransactionModeToReceiveOnly()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services).Build();

            options.TransactionMode.Should().Be(TransactionMode.ReceiveOnly);
        }

        [Fact]
        public void MustBuildNonNullReliabilityOptions()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services).Build();

            options.Reliability.Should().NotBeNull();
        }

        [Fact]
        public void MustBuildNonNullRecoveryOptions()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services).Build();

            options.Recovery.Should().NotBeNull();
        }

        [Fact]
        public void MustReflectWithTransactionMode()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure)
                .Build();

            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
        }

        [Fact]
        public void MustBuildNonNullReliabilityOptionsWhenAddReliabilityOptionsActionProvided()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .AddReliabilityOptions(r => r.WithOutboxRouting())
                .Build();

            options.Reliability.Should().NotBeNull();
            options.Reliability.RouteMessagesToOutbox.Should().BeTrue();
        }

        [Fact]
        public void MustBuildNonNullReliabilityOptionsWhenAddReliabilityOptionsActionIsNull()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .AddReliabilityOptions(null)
                .Build();

            options.Reliability.Should().NotBeNull();
        }

        [Fact]
        public void MustBuildNonNullRecoveryOptionsWhenAddRecoveryOptionsActionProvided()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(r => r.WithMaxRetryAttempts(9))
                .Build();

            options.Recovery.Should().NotBeNull();
            options.Recovery.MaxRetryAttempts.Should().Be(9);
        }

        [Fact]
        public void MustBuildNonNullRecoveryOptionsWhenAddRecoveryOptionsActionIsNull()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(null)
                .Build();

            options.Recovery.Should().NotBeNull();
        }

        [Fact]
        public void MustTakeConfigBranchWhenStaticFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration();

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            AssertConfigBranchTaken(options);
        }

        [Fact]
        public void MustTakeConfigBranchWhenInstanceFromConfigForwardsServicesAndConfiguration()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration();
            var builder = new MessageBrokerOptionsBuilder(services, configuration);

            var options = builder.FromConfig();

            AssertConfigBranchTaken(options);
        }

        // INVARIANT: production binds the populated section via IConfigurationSection.Get<MessageBrokerOptions>(),
        // whose internal-set TransactionMode stays at its type default (None); the fluent else-branch (which would
        // otherwise apply the ReceiveOnly default) is skipped. Reliability and Recovery come back null from the bind
        // and are then defaulted to non-null instances by Build().
        private static void AssertConfigBranchTaken(MessageBrokerOptions options)
        {
            options.Should().NotBeNull();
            options.TransactionMode.Should().Be(TransactionMode.None);
            options.Reliability.Should().NotBeNull();
            options.Recovery.Should().NotBeNull();
        }

        private static IConfiguration BuildConfiguration()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = nameof(TransactionMode.FullAtomicityViaInfrastructure)
                })
                .Build();
    }
}
