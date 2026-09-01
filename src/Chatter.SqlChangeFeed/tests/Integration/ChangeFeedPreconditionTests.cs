using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.SqlChangeFeed.Scripts;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // Precondition-gate proof for the SqlChangeFeed integration suite (#353). The install procedure the production
    // migration creates must refuse an unusable watched table with an error naming the REAL cause and the table,
    // BEFORE it creates any Service Broker object — so a refused install leaves no partial state behind rather than
    // failing halfway through with a queue and a service already created.
    //
    // Gated by [RequiresDockerFact]. Each case owns a DISTINCT row DTO + table (object names the migration derives
    // come from typeof(TRow).Name, so a shared DTO across cases would collide).
    [Trait("Category", "Integration")]
    [Collection(SqlChangeFeedCollection.Name)]
    public class ChangeFeedPreconditionTests
    {
        private const string TableWithoutPrimaryKeyName = "ChangeFeedNoPrimaryKey";
        private const string MissingTableName = "ChangeFeedMissingTable";

        private readonly SqlChangeFeedFixture _fixture;

        public ChangeFeedPreconditionTests(SqlChangeFeedFixture fixture)
            => _fixture = fixture;

        public sealed class NoPrimaryKeyRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        public sealed class MissingTableRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        [RequiresDockerFact]
        public async Task ChangeFeedInstallRefusesAWatchedTableWithoutAPrimaryKeyAndCreatesNothing()
        {
            var connectionString = _fixture.GetAppConnectionString();
            await ChangeFeedTableProvisioning.DropTableAsync(connectionString, TableWithoutPrimaryKeyName, CancellationToken.None);
            await ChangeFeedTableProvisioning.CreateTableWithoutPrimaryKeyAsync(connectionString, TableWithoutPrimaryKeyName, CancellationToken.None);

            try
            {
                var refusal = await RunMigrationExpectingRefusalAsync<NoPrimaryKeyRow>(connectionString, TableWithoutPrimaryKeyName);

                refusal.Should().Contain("PRIMARY KEY", "the error must name the real cause")
                       .And.Contain($"[dbo].[{TableWithoutPrimaryKeyName}]", "the error must name the watched table");
            }
            finally
            {
                await ChangeFeedTableProvisioning.DropTableAsync(connectionString, TableWithoutPrimaryKeyName, CancellationToken.None);
            }
        }

        [RequiresDockerFact]
        public async Task ChangeFeedInstallRefusesAMissingWatchedTableAndCreatesNothing()
        {
            var connectionString = _fixture.GetAppConnectionString();
            await ChangeFeedTableProvisioning.DropTableAsync(connectionString, MissingTableName, CancellationToken.None);

            var refusal = await RunMigrationExpectingRefusalAsync<MissingTableRow>(connectionString, MissingTableName);

            refusal.Should().Contain("does not exist", "the error must name the real cause")
                   .And.Contain($"[dbo].[{MissingTableName}]", "the error must name the watched table");
        }

        // Runs the PRODUCTION migration expecting the install procedure's precondition gate to refuse it, asserts no
        // Service Broker object survived the refusal, and returns the raised message for cause/table assertions.
        private async Task<string> RunMigrationExpectingRefusalAsync<TRow>(string connectionString, string tableName)
            where TRow : class, IMessage, new()
        {
            // Object names the production migration derives from the row type name.
            var rowName = typeof(TRow).Name;
            var triggerName = $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{rowName}";
            var queueName = $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{rowName}";
            var serviceName = $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{rowName}";

            var harness = ChatterChangeFeedHarness<TRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                tableName);
            try
            {
                var refusal = await FluentActions.Awaiting(() => harness.RunMigrationsAsync())
                    .Should().ThrowAsync<SqlException>("the install procedure must raise a precondition error");

                // The gate runs before the Service Broker section, so a refusal leaves no partial state behind.
                (await ChangeFeedTableProvisioning.QueueExistsAsync(connectionString, queueName, CancellationToken.None))
                    .Should().BeFalse("a refused install must not leave a change-feed queue behind");
                (await ChangeFeedTableProvisioning.ServiceExistsAsync(connectionString, serviceName, CancellationToken.None))
                    .Should().BeFalse("a refused install must not leave a change-feed service behind");
                (await ChangeFeedTableProvisioning.TriggerExistsAsync(connectionString, triggerName, CancellationToken.None))
                    .Should().BeFalse("a refused install must not leave a change-feed trigger behind");

                return refusal.Which.Message;
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }
    }
}
