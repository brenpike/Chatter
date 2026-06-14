using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.SqlChangeFeed.Scripts;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // Clean-uninstall proof for the SqlChangeFeed integration suite (STEP-C7). After the REAL production migration
    // installs the change-feed objects, this test asserts the trigger + Service Broker queue/service it created
    // actually exist, runs the production uninstall (UninstallSqlDependencies via the harness), and asserts they
    // are gone — proving the migration's install and uninstall are symmetric against a live SQL Server.
    //
    // Gated by [RequiresDockerFact]. Owns a DISTINCT row DTO + table (object names derive from typeof(TRow).Name).
    [Trait("Category", "Integration")]
    [Collection(SqlChangeFeedCollection.Name)]
    public class ChangeFeedUninstallTests
    {
        private const string TableName = "ChangeFeedUninstall";

        private readonly SqlChangeFeedFixture _fixture;

        public ChangeFeedUninstallTests(SqlChangeFeedFixture fixture)
            => _fixture = fixture;

        public sealed class UninstallRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        [RequiresDockerFact]
        public async Task ChangeFeedUninstallRemovesTheTriggerQueueAndServiceTheMigrationInstalled()
        {
            var connectionString = _fixture.GetAppConnectionString();
            await ChangeFeedTableProvisioning.CreateTableAsync(connectionString, TableName, CancellationToken.None);

            // Object names the production migration derives from the row type name.
            var rowName = typeof(UninstallRow).Name;
            var triggerName = $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{rowName}";
            var queueName = $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{rowName}";
            var serviceName = $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{rowName}";

            var harness = ChatterChangeFeedHarness<UninstallRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                TableName);
            try
            {
                await harness.RunMigrationsAsync();

                // Post-install: the migration must have created the trigger, queue, and service.
                (await ChangeFeedTableProvisioning.TriggerExistsAsync(connectionString, triggerName, CancellationToken.None))
                    .Should().BeTrue("the migration must install the change-feed trigger");
                (await ChangeFeedTableProvisioning.QueueExistsAsync(connectionString, queueName, CancellationToken.None))
                    .Should().BeTrue("the migration must install the change-feed queue");
                (await ChangeFeedTableProvisioning.ServiceExistsAsync(connectionString, serviceName, CancellationToken.None))
                    .Should().BeTrue("the migration must install the change-feed service");

                await harness.UninstallAsync();

                // Post-uninstall: the trigger, queue, and service must be gone.
                (await ChangeFeedTableProvisioning.TriggerExistsAsync(connectionString, triggerName, CancellationToken.None))
                    .Should().BeFalse("the uninstall must remove the change-feed trigger");
                (await ChangeFeedTableProvisioning.QueueExistsAsync(connectionString, queueName, CancellationToken.None))
                    .Should().BeFalse("the uninstall must remove the change-feed queue");
                (await ChangeFeedTableProvisioning.ServiceExistsAsync(connectionString, serviceName, CancellationToken.None))
                    .Should().BeFalse("the uninstall must remove the change-feed service");
            }
            finally
            {
                await harness.DisposeAsync();
                await ChangeFeedTableProvisioning.DropTableAsync(connectionString, TableName, CancellationToken.None);
            }
        }
    }
}
