using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.SqlChangeFeed.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.UsingSqlChangeFeedExtensions
{
    /// <summary>
    /// Pins that the receive-attempt limit configured on a change feed reaches the receiver options of the
    /// feed's queue receiver, through both the generic and the row-type overloads of AddSqlChangeFeed.
    /// </summary>
    public class WhenAddingSqlChangeFeed : Testing.Core.Context
    {
        private const string _connectionString = "Server=test;Database=test;Trusted_Connection=True;";
        private const string _databaseName = "test";
        private const string _tableName = "FakeRows";
        private const string _feedQueueName = "Chatter_Queue_FakeRowData";
        private const int _defaultMaxReceiveAttempts = 10;

        private static IChatterBuilder NewBareBuilder(IServiceCollection services)
            => ChatterBuilder.Create(services, new ConfigurationBuilder().Build(), AssemblySourceFilterBuilder.New().Build());

        private static ReceiverOptions FeedReceiverOptions(IServiceCollection services)
            => (services.Last(d => d.ServiceType == typeof(IDiscoveredReceiverRegistry))
                        .ImplementationInstance as IDiscoveredReceiverRegistry)
                   .DiscoveredReceivers
                   .Single(receiver => receiver.MessageReceiverPath == _feedQueueName);

        [Fact]
        public void MustApplyTheConfiguredMaxReceiveAttemptsToTheFeedReceiver()
        {
            var services = new ServiceCollection();

            NewBareBuilder(services).AddSqlChangeFeed<FakeRowData>(_connectionString, _databaseName, _tableName,
                                                                   o => o.WithMaxReceiveAttempts(3));

            FeedReceiverOptions(services).MaxReceiveAttempts.Should().Be(3);
        }

        [Fact]
        public void MustApplyTheConfiguredMaxReceiveAttemptsToTheFeedReceiverWhenTheRowTypeIsPassedAsAType()
        {
            var services = new ServiceCollection();

            NewBareBuilder(services).AddSqlChangeFeed(typeof(FakeRowData), _connectionString, _databaseName, _tableName,
                                                      o => o.WithMaxReceiveAttempts(3));

            FeedReceiverOptions(services).MaxReceiveAttempts.Should().Be(3);
        }

        [Fact]
        public void MustApplyTheDefaultMaxReceiveAttemptsToTheFeedReceiverWhenNoneIsConfigured()
        {
            var services = new ServiceCollection();

            NewBareBuilder(services).AddSqlChangeFeed<FakeRowData>(_connectionString, _databaseName, _tableName);

            FeedReceiverOptions(services).MaxReceiveAttempts.Should().Be(_defaultMaxReceiveAttempts);
        }
    }
}
