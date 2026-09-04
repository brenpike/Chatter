#nullable disable

using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.DependencyInjection.UsingChatterMessageBrokerExtensions
{
    /// <summary>
    /// Verifies the documented DI entry point actually reads the <c>Chatter:MessageBrokers</c> section.
    /// <c>AddMessageBrokerOptions</c> hands the builder an <see cref="IConfiguration"/> but no
    /// <see cref="IConfigurationSection"/>, so the section that gets bound has to be resolved by the builder
    /// itself; without that resolution every key under <c>Chatter:MessageBrokers</c> is silently discarded for
    /// a consumer configuring Chatter through <c>AddMessageBrokers</c>.
    /// </summary>
    public class WhenBindingMessageBrokerConfiguration : Testing.Core.Context
    {
        [Fact]
        public void MustHonourConfiguredTransactionModeWhenReachedThroughAddMessageBrokerOptions()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration(new Dictionary<string, string>
            {
                [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = nameof(TransactionMode.FullAtomicityViaInfrastructure)
            });

            var options = services.AddMessageBrokerOptions(configuration).Build();

            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
        }

        [Fact]
        public void MustDefaultTransactionModeToReceiveOnlyWhenConfigurationCarriesNoBrokerSection()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder().Build();

            var options = services.AddMessageBrokerOptions(configuration).Build();

            options.TransactionMode.Should().Be(TransactionMode.ReceiveOnly);
        }

        [Fact]
        public void MustHonourNestedRecoveryKeyWhenReachedThroughAddMessageBrokerOptions()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration(new Dictionary<string, string>
            {
                [$"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts"] = "42"
            });

            var options = services.AddMessageBrokerOptions(configuration).Build();

            options.Recovery.MaxRetryAttempts.Should().Be(42);
        }

        private static IConfiguration BuildConfiguration(Dictionary<string, string> values)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
    }
}
