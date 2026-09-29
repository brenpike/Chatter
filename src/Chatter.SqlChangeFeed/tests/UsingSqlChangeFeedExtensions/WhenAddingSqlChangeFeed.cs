using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.SqlChangeFeed.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.UsingSqlChangeFeedExtensions
{
    /// <summary>
    /// Pins that the receive-attempt limit configured on a change feed reaches the receiver options of the
    /// feed's queue receiver, through both the generic and the row-type overloads of AddSqlChangeFeed, and that
    /// change feeds share the one SQL Server Service Broker transport configuration a host supports while each
    /// feed keeps its own receiver settings.
    /// </summary>
    public class WhenAddingSqlChangeFeed : Testing.Core.Context
    {
        private const string _connectionString = "Server=test;Database=test;Trusted_Connection=True;";
        private const string _otherConnectionString = "Server=other;Database=test;Trusted_Connection=True;";
        private const string _databaseName = "test";
        private const string _tableName = "FakeRows";
        private const string _secondTableName = "SecondFakeRows";
        private const string _feedQueueName = "Chatter_Queue_FakeRowData";
        private const string _secondFeedQueueName = "Chatter_Queue_SecondFakeRowData";
        private const int _defaultMaxReceiveAttempts = 10;

        public sealed class SecondFakeRowData : Chatter.CQRS.IMessage
        {
            public int Id { get; set; }
        }

        private static IChatterBuilder NewBareBuilder(IServiceCollection services)
            => ChatterBuilder.Create(services, new ConfigurationBuilder().Build(), AssemblySourceFilterBuilder.New().Build());

        private static ReceiverOptions FeedReceiverOptions(IServiceCollection services)
            => ReceiverOptionsForQueue(services, _feedQueueName);

        private static ReceiverOptions ReceiverOptionsForQueue(IServiceCollection services, string queueName)
            => (services.Last(d => d.ServiceType == typeof(IDiscoveredReceiverRegistry))
                        .ImplementationInstance as IDiscoveredReceiverRegistry)
                   .DiscoveredReceivers
                   .Single(receiver => receiver.MessageReceiverPath == queueName);

        private static SqlServiceBrokerOptions FeedDefaultTransportOptionsExceptReceiverTimeout(int receiverTimeoutInMilliseconds)
            => new SqlServiceBrokerOptions(_connectionString,
                                           "application/json; charset=utf-16",
                                           receiverTimeoutInMilliseconds,
                                           int.MaxValue,
                                           coversationEncryption: false,
                                           compressMessageBody: true,
                                           cleanupOnEndConversation: false);

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

        [Fact]
        public void MustShareOneTransportConfigurationAndKeepReceiverSettingsPerFeedWhenTwoFeedsUseTheSameConnectionString()
        {
            var services = new ServiceCollection();
            var builder = NewBareBuilder(services);

            builder.AddSqlChangeFeed<FakeRowData>(_connectionString, _databaseName, _tableName,
                                                  o => o.WithMaxReceiveAttempts(3));
            builder.AddSqlChangeFeed<SecondFakeRowData>(_connectionString, _databaseName, _secondTableName,
                                                        o => o.WithMaxReceiveAttempts(7));

            services.Count(d => d.ServiceType == typeof(SqlServiceBrokerOptions)).Should().Be(1);
            ReceiverOptionsForQueue(services, _feedQueueName).MaxReceiveAttempts.Should().Be(3);
            ReceiverOptionsForQueue(services, _secondFeedQueueName).MaxReceiveAttempts.Should().Be(7);
        }

        [Fact]
        public void MustRefuseASecondFeedWhoseConnectionStringDivergesWithoutRevealingEitherConnectionString()
        {
            var services = new ServiceCollection();
            var builder = NewBareBuilder(services);
            builder.AddSqlChangeFeed<FakeRowData>(_connectionString, _databaseName, _tableName);

            var addSecondFeed = () => builder.AddSqlChangeFeed<SecondFakeRowData>(_otherConnectionString, _databaseName, _secondTableName);

            var refusal = addSecondFeed.Should().Throw<NotSupportedException>().Which;
            refusal.Message.Should().Contain(nameof(SqlServiceBrokerOptions.ConnectionString));
            refusal.Message.Should().NotContain(_connectionString).And.NotContain(_otherConnectionString);
        }

        [Fact]
        public void MustRefuseAFeedWhoseTransportOptionsDivergeFromAnExplicitlyAddedSqlServiceBroker()
        {
            var services = new ServiceCollection();
            var builder = NewBareBuilder(services);
            builder.AddSqlServiceBroker(ssb => ssb.AddSqlServiceBrokerOptions(FeedDefaultTransportOptionsExceptReceiverTimeout(5000)));

            var addFeed = () => builder.AddSqlChangeFeed<FakeRowData>(_connectionString, _databaseName, _tableName);

            addFeed.Should().Throw<NotSupportedException>()
                   .Which.Message.Should().Contain(nameof(SqlServiceBrokerOptions.ReceiverTimeoutInMilliseconds));
        }
    }
}
