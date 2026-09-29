using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.SqlChangeFeed.Configuration;
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
    /// feed keeps its own receiver settings. Also pins that a refused feed registers nothing and raises its refusal
    /// unwrapped through either overload, and which receiver a feed registers.
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

        private const string _genericOverload = "generic overload";
        private const string _rowTypeOverload = "row type overload";

        public static TheoryData<string, string, Type> FeedRefusals()
        {
            var refusals = new TheoryData<string, string, Type>();
            foreach (var overload in new[] { _genericOverload, _rowTypeOverload })
            {
                refusals.Add(overload, "blank connection string", typeof(ArgumentNullException));
                refusals.Add(overload, "blank table name", typeof(ArgumentNullException));
                refusals.Add(overload, "malformed connection string", typeof(ArgumentException));
                refusals.Add(overload, "no database", typeof(InvalidOperationException));
                refusals.Add(overload, "colliding object names", typeof(ChangeFeedObjectNameCollisionException));
                refusals.Add(overload, "blank message body type", typeof(ArgumentNullException));
                refusals.Add(overload, "divergent transport", typeof(NotSupportedException));
            }
            return refusals;
        }

        private static Action<string, string, string, Action<SqlChangeFeedOptionsBuilder>> FeedAdderThrough(IChatterBuilder builder, string overload)
            => overload switch
            {
                _genericOverload => (connectionString, databaseName, tableName, optionsBuilder)
                    => builder.AddSqlChangeFeed<FakeRowData>(connectionString, databaseName, tableName, optionsBuilder),
                _rowTypeOverload => (connectionString, databaseName, tableName, optionsBuilder)
                    => builder.AddSqlChangeFeed(typeof(FakeRowData), connectionString, databaseName, tableName, optionsBuilder),
                _ => throw new ArgumentOutOfRangeException(nameof(overload), overload, "Unknown AddSqlChangeFeed overload."),
            };

        private static void AddRefusedFeed(Action<string, string, string, Action<SqlChangeFeedOptionsBuilder>> addFeed, string refusal)
        {
            switch (refusal)
            {
                case "blank connection string":
                    addFeed(" ", _databaseName, _tableName, null);
                    return;
                case "blank table name":
                    addFeed(_connectionString, _databaseName, " ", null);
                    return;
                case "malformed connection string":
                    addFeed("not a connection string", _databaseName, _tableName, null);
                    return;
                case "no database":
                    addFeed("Server=test;Trusted_Connection=True;", null, _tableName, null);
                    return;
                case "colliding object names":
                    addFeed(_connectionString, _databaseName, _tableName,
                            o => o.WithChangeFeedDeadLetterServiceName("Chatter_Service_FakeRowData"));
                    return;
                case "blank message body type":
                    addFeed(_connectionString, _databaseName, _tableName, o => o.WithMessageBodyType(" "));
                    return;
                case "divergent transport":
                    addFeed(_otherConnectionString, _databaseName, _tableName, null);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "Unknown change feed refusal.");
            }
        }

        private static IDiscoveredReceiverRegistry EffectiveDiscoveredReceiverRegistry(IServiceCollection services)
            => services.LastOrDefault(d => d.ServiceType == typeof(IDiscoveredReceiverRegistry))?
                       .ImplementationInstance as IDiscoveredReceiverRegistry;

        [Theory]
        [MemberData(nameof(FeedRefusals))]
        public void MustLeaveTheServiceCollectionAndDiscoveredReceiversExactlyAsTheyWereWhenAFeedIsRefused(string overload, string refusal, Type expectedRefusal)
        {
            var services = new ServiceCollection();
            var builder = NewBareBuilder(services);
            builder.AddSqlChangeFeed<SecondFakeRowData>(_connectionString, _databaseName, _secondTableName);
            var beforeTheRefusedCall = services.ToList();
            var discoveredBeforeTheRefusedCall = EffectiveDiscoveredReceiverRegistry(services).DiscoveredReceivers.ToList();

            Action addRefusedFeed = () => AddRefusedFeed(FeedAdderThrough(builder, overload), refusal);

            addRefusedFeed.Should().Throw<Exception>().Which.Should().BeOfType(expectedRefusal);
            services.Should().Equal(beforeTheRefusedCall,
                                    "a refused feed must not register anything - the same descriptors must still sit in the same slots");
            EffectiveDiscoveredReceiverRegistry(services).DiscoveredReceivers.Should().Equal(discoveredBeforeTheRefusedCall);
        }

        [Fact]
        public void MustRefuseANullRowTypeNamingTheRowTypeParameter()
        {
            var builder = NewBareBuilder(new ServiceCollection());

            var addFeed = () => builder.AddSqlChangeFeed(null, _connectionString, _databaseName, _tableName);

            addFeed.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("rowChangedDataType");
        }

        [Fact]
        public void MustRefuseARowTypeThatIsNotAMessageAsAnInvalidArgument()
        {
            var builder = NewBareBuilder(new ServiceCollection());

            var addFeed = () => builder.AddSqlChangeFeed(typeof(object), _connectionString, _databaseName, _tableName);

            addFeed.Should().Throw<Exception>().Which.Should().BeOfType<ArgumentException>();
        }

        [Fact]
        public void MustRegisterASqlDependencyManagerForTheFeedsOptions()
        {
            var services = new ServiceCollection();

            NewBareBuilder(services).AddSqlChangeFeed<FakeRowData>(_connectionString, _databaseName, _tableName);

            var registration = services.Should().ContainSingle(d => d.ServiceType == typeof(ISqlDependencyManager<FakeRowData>)).Which;
            registration.Lifetime.Should().Be(ServiceLifetime.Scoped);
            registration.ImplementationFactory(null).Should().BeOfType<SqlDependencyManager<FakeRowData>>()
                        .Which.Options.TableName.Should().Be(_tableName);
        }

        [Fact]
        public void MustRegisterTheChangeFeedReceiverAsTheFeedsOnlyReceiverWhenRowChangeEventsAreEmitted()
        {
            var services = new ServiceCollection();

            NewBareBuilder(services).AddSqlChangeFeed<FakeRowData>(_connectionString, _databaseName, _tableName);

            services.Should().ContainSingle(d => d.ServiceType == typeof(IBrokeredMessageReceiver<ProcessChangeFeedCommand<FakeRowData>>))
                    .Which.ImplementationType.Should().Be(typeof(ChangeFeedReceiver<FakeRowData>));
        }

        [Fact]
        public void MustKeepTheBrokeredMessageReceiverWhenTableChangesAreProcessedManually()
        {
            var services = new ServiceCollection();

            NewBareBuilder(services).AddSqlChangeFeed<FakeRowData>(_connectionString, _databaseName, _tableName,
                                                                   o => o.ProcessTableChangesManually());

            services.Should().ContainSingle(d => d.ServiceType == typeof(IBrokeredMessageReceiver<ProcessChangeFeedCommand<FakeRowData>>))
                    .Which.ImplementationType.Should().Be(typeof(BrokeredMessageReceiver<ProcessChangeFeedCommand<FakeRowData>>));
        }
    }
}
