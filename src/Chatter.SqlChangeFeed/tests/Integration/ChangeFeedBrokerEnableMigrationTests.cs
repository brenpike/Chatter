using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // Broker-enable proof for the SqlChangeFeed integration suite (STEP-C8). The collection fixture's shared app
    // database is created with Service Broker ALREADY enabled, so it cannot prove the production migration's
    // ENABLE_BROKER branch fires. This test creates its OWN fresh database WITHOUT pre-enabling the broker, runs
    // the REAL UseChangeFeedSqlMigrationsAsync, and asserts: (1) sys.databases.is_broker_enabled flips 0 -> 1, (2) the
    // install otherwise succeeds, and (3) one INSERT notification is delivered through the change-feed path on the
    // newly-broker-enabled database.
    //
    // Gated by [RequiresDockerFact]. Owns a DISTINCT row DTO + table (object names derive from typeof(TRow).Name)
    // AND a DISTINCT dedicated database created/dropped here.
    [Trait("Category", "Integration")]
    [Collection(SqlChangeFeedCollection.Name)]
    public class ChangeFeedBrokerEnableMigrationTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private const string TableName = "ChangeFeedBrokerEnable";

        // DISTINCT dedicated database, NOT the fixture's pre-broker-enabled shared database, so the broker bit
        // starts at 0 and the migration's ENABLE_BROKER branch is genuinely exercised.
        private const string DatabaseName = "chatter_scf_it_brokerenable";

        private readonly SqlChangeFeedFixture _fixture;

        public ChangeFeedBrokerEnableMigrationTests(SqlChangeFeedFixture fixture)
            => _fixture = fixture;

        public sealed class BrokerEnableRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        [RequiresDockerFact]
        public async Task MigrationEnablesServiceBrokerOnAFreshDatabaseAndDeliversANotification()
        {
            var masterConnectionString = _fixture.GetMasterConnectionString();

            var appConnectionString = _fixture.GetConnectionStringForDatabase(DatabaseName);

            await ChangeFeedTableProvisioning.DropDatabaseAsync(masterConnectionString, DatabaseName, CancellationToken.None);
            await ChangeFeedTableProvisioning.CreateDatabaseAsync(masterConnectionString, DatabaseName, CancellationToken.None);
            // A freshly created database inherits its broker state from 'model' (broker-enabled on the test image),
            // so explicitly disable the broker to genuinely exercise the migration's ENABLE_BROKER branch.
            await ChangeFeedTableProvisioning.DisableBrokerAsync(masterConnectionString, DatabaseName, CancellationToken.None);
            try
            {
                // Precondition: the fresh database starts with Service Broker DISABLED.
                (await ChangeFeedTableProvisioning.IsBrokerEnabledAsync(masterConnectionString, DatabaseName, CancellationToken.None))
                    .Should().BeFalse("the fresh database must start with Service Broker disabled so the migration's ENABLE_BROKER branch is exercised");

                await ChangeFeedTableProvisioning.CreateTableAsync(appConnectionString, TableName, CancellationToken.None);

                var harness = ChatterChangeFeedHarness<BrokerEnableRow>.Build(
                    appConnectionString,
                    DatabaseName,
                    TableName);
                try
                {
                    await harness.RunMigrationsAsync();

                    // The migration must have flipped the broker bit on the fresh database.
                    (await ChangeFeedTableProvisioning.IsBrokerEnabledAsync(masterConnectionString, DatabaseName, CancellationToken.None))
                        .Should().BeTrue("the migration must enable Service Broker on a database where it was disabled");

                    await harness.StartAsync();

                    // And the change feed must work end-to-end on the newly-broker-enabled database.
                    await ChangeFeedTableProvisioning.InsertRowAsync(appConnectionString, TableName, 1, "broker-enabled", "value", CancellationToken.None);

                    var inserted = await harness.Signals.WaitForHandledAsync<RowInsertedEvent<BrokerEnableRow>>(HandlerWait);
                    inserted.Inserted.Should().NotBeNull("the change feed must deliver a notification after enabling the broker");
                    inserted.Inserted.Id.Should().Be(1);
                    inserted.Inserted.Name.Should().Be("broker-enabled");
                }
                finally
                {
                    await harness.DisposeAsync();
                }
            }
            finally
            {
                await ChangeFeedTableProvisioning.DropDatabaseAsync(masterConnectionString, DatabaseName, CancellationToken.None);
            }
        }
    }
}
