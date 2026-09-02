using Chatter.MessageBrokers.Receiving;
using Chatter.SqlChangeFeed.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Configuration.UsingSqlChangeFeedOptionsBuilder
{
    public class WhenConfiguring : Testing.Core.Context
    {
        private const string ConnStrWithCatalog = "Server=.;Database=DbFromConnStr;";
        private const string ConnStrWithoutCatalog = "Server=.;";

        private static SqlChangeFeedOptionsBuilder CreateBuilder(
            IServiceCollection services = null,
            string connectionString = ConnStrWithCatalog,
            string databaseName = "Db",
            string tableName = "Table")
            => new SqlChangeFeedOptionsBuilder(
                services ?? new ServiceCollection(),
                connectionString,
                databaseName,
                tableName);

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServicesIsNull()
            => FluentActions.Invoking(() => new SqlChangeFeedOptionsBuilder(null, ConnStrWithCatalog, "Db", "Table"))
                .Should().Throw<ArgumentNullException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowArgumentNullExceptionForConnectionStringWhenNullOrWhitespace(string connectionString)
            => FluentActions.Invoking(() => new SqlChangeFeedOptionsBuilder(new ServiceCollection(), connectionString, "Db", "Table"))
                .Should().Throw<ArgumentNullException>()
                .And.ParamName.Should().Be("connectionString");

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowArgumentNullExceptionForTableNameWhenNullOrWhitespace(string tableName)
            => FluentActions.Invoking(() => new SqlChangeFeedOptionsBuilder(new ServiceCollection(), ConnStrWithCatalog, "Db", tableName))
                .Should().Throw<ArgumentNullException>()
                .And.ParamName.Should().Be("tableName");

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustNotThrowWhenDatabaseNameIsNullOrWhitespace(string databaseName)
            => FluentActions.Invoking(() => new SqlChangeFeedOptionsBuilder(new ServiceCollection(), ConnStrWithCatalog, databaseName, "Table"))
                .Should().NotThrow();

        [Fact]
        public void MustUseInitialCatalogFromConnectionStringWhenDatabaseNameNotSupplied()
        {
            var options = CreateBuilder(connectionString: ConnStrWithCatalog, databaseName: null).Build();
            options.DatabaseName.Should().Be("DbFromConnStr");
        }

        [Fact]
        public void MustUseExplicitDatabaseNameWhenConnectionStringHasNoInitialCatalog()
        {
            var options = CreateBuilder(connectionString: ConnStrWithoutCatalog, databaseName: null)
                .WithNameOfDatabaseToWatch("ExplicitDb")
                .Build();
            options.DatabaseName.Should().Be("ExplicitDb");
        }

        [Fact]
        public void MustPreferBuilderDatabaseNameOverConnectionStringInitialCatalog()
        {
            var options = CreateBuilder(connectionString: ConnStrWithCatalog, databaseName: null)
                .WithNameOfDatabaseToWatch("ExplicitDb")
                .Build();
            options.DatabaseName.Should().Be("ExplicitDb");
        }

        [Fact]
        public void MustThrowInvalidOperationExceptionWhenNeitherConnectionStringCatalogNorDatabaseNameSupplied()
        {
            var build = CreateBuilder(connectionString: ConnStrWithoutCatalog, databaseName: null);

            FluentActions.Invoking(() => build.Build())
                .Should().Throw<InvalidOperationException>()
                // INVARIANT: production interpolates the VALUE of _databaseName (null/empty at throw
                // time) rather than nameof(_databaseName), so the message ends "...via _connectionString or ".
                .WithMessage("*Cannot build SqlChangeFeedOptions if a database is not specified via _connectionString or*");
        }

        [Fact]
        public void MustNotContainDatabaseNameIdentifierTokenAfterOrInBuildFailureMessage()
        {
            var build = CreateBuilder(connectionString: ConnStrWithoutCatalog, databaseName: null);

            var thrown = Assert.Throws<InvalidOperationException>(() => build.Build());

            var afterOr = thrown.Message.Substring(thrown.Message.LastIndexOf("or ", StringComparison.Ordinal));
            afterOr.Should().NotContain("_databaseName");
        }

        [Fact]
        public void MustDefaultSchemaNameToDbo()
            => CreateBuilder().Build().SchemaName.Should().Be("dbo");

        [Fact]
        public void MustMapWithSchemaToSchemaName()
            => CreateBuilder().WithSchema("custom").Build().SchemaName.Should().Be("custom");

        [Fact]
        public void MustDefaultChangeFeedTriggerTypesToInsertUpdateDelete()
            => CreateBuilder().Build().ChangeFeedTriggerTypes
                .Should().Be(ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete);

        [Fact]
        public void MustMapWithTypesOfChangesToWatchToChangeFeedTriggerTypes()
            => CreateBuilder().WithTypesOfChangesToWatch(ChangeTypes.Insert).Build()
                .ChangeFeedTriggerTypes.Should().Be(ChangeTypes.Insert);

        [Fact]
        public void MustDefaultProcessChangeFeedCommandViaChatterToTrue()
            => CreateBuilder().Build().ProcessChangeFeedCommandViaChatter.Should().BeTrue();

        [Fact]
        public void MustMapProcessTableChangesManuallyToProcessChangeFeedCommandViaChatterFalse()
            => CreateBuilder().ProcessTableChangesManually().Build()
                .ProcessChangeFeedCommandViaChatter.Should().BeFalse();

        [Fact]
        public void MustMapEmitRowChangeEventsToProcessChangeFeedCommandViaChatterTrue()
            => CreateBuilder().ProcessTableChangesManually().EmitRowChangeEvents().Build()
                .ProcessChangeFeedCommandViaChatter.Should().BeTrue();

        [Fact]
        public void MustMapWithChangeFeedQueueNameToChangeFeedQueueName()
            => CreateBuilder().WithChangeFeedQueueName("queue").Build()
                .ChangeFeedQueueName.Should().Be("queue");

        [Fact]
        public void MustMapWithReceiverTimeoutInMillisecondsToServiceBrokerOptions()
            => CreateBuilder().WithReceiverTimeoutInMilliseconds(5000).Build()
                .ServiceBrokerOptions.ReceiverTimeoutInMilliseconds.Should().Be(5000);

        [Fact]
        public void MustMapWithMaxReceiveAttemptsToReceiverOptions()
            => CreateBuilder().WithMaxReceiveAttempts(3).Build()
                .ReceiverOptions.MaxReceiveAttempts.Should().Be(3);

        [Fact]
        public void MustMapWithErrorQueueNameToReceiverOptionsErrorQueuePath()
            => CreateBuilder().WithErrorQueueName("error-queue").Build()
                .ReceiverOptions.ErrorQueuePath.Should().Be("error-queue");

        [Fact]
        public void MustMapWithTransactionModeToReceiverOptionsTransactionMode()
            => CreateBuilder().WithTransactionMode(TransactionMode.ReceiveOnly).Build()
                .ReceiverOptions.TransactionMode.Should().Be(TransactionMode.ReceiveOnly);

        [Fact]
        public void MustMapWithChangeFeedDeadLetterServiceNameToReceiverOptionsDeadLetterQueuePath()
            => CreateBuilder().WithChangeFeedDeadLetterServiceName("dlq-service").Build()
                .ReceiverOptions.DeadLetterQueuePath.Should().Be("dlq-service");

        [Fact]
        public void MustMapWithChangeFeedDeadLetterServiceNameToChangeFeedDeadLetterServiceName()
            => CreateBuilder().WithChangeFeedDeadLetterServiceName("dlq-service").Build()
                .ChangeFeedDeadLetterServiceName.Should().Be("dlq-service");

        [Fact]
        public void MustLeaveChangeFeedDeadLetterServiceNameNullWhenNotConfigured()
            => CreateBuilder().Build().ChangeFeedDeadLetterServiceName.Should().BeNull();

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithSchema()
        {
            var builder = CreateBuilder();
            builder.WithSchema("s").Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithNameOfDatabaseToWatch()
        {
            var builder = CreateBuilder();
            builder.WithNameOfDatabaseToWatch("db").Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithTypesOfChangesToWatch()
        {
            var builder = CreateBuilder();
            builder.WithTypesOfChangesToWatch(ChangeTypes.Insert).Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromProcessTableChangesManually()
        {
            var builder = CreateBuilder();
            builder.ProcessTableChangesManually().Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromEmitRowChangeEvents()
        {
            var builder = CreateBuilder();
            builder.EmitRowChangeEvents().Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithChangeFeedQueueName()
        {
            var builder = CreateBuilder();
            builder.WithChangeFeedQueueName("queue").Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithReceiverTimeoutInMilliseconds()
        {
            var builder = CreateBuilder();
            builder.WithReceiverTimeoutInMilliseconds(1).Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithMaxReceiveAttempts()
        {
            var builder = CreateBuilder();
            builder.WithMaxReceiveAttempts(1).Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithErrorQueueName()
        {
            var builder = CreateBuilder();
            builder.WithErrorQueueName("error").Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithTransactionMode()
        {
            var builder = CreateBuilder();
            builder.WithTransactionMode(TransactionMode.None).Should().BeSameAs(builder);
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithChangeFeedDeadLetterServiceName()
        {
            var builder = CreateBuilder();
            builder.WithChangeFeedDeadLetterServiceName("dlq").Should().BeSameAs(builder);
        }
    }
}
