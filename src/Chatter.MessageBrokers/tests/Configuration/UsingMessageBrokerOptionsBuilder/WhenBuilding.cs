using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
    }
}
