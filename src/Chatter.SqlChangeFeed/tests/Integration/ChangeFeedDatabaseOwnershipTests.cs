using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // Privilege-posture proof for the SqlChangeFeed integration suite (#348, #351). The production install script's
    // broker-enable branch used to transfer ownership of the consumer's database to [sa], and used to enable the
    // broker WITHOUT ROLLBACK IMMEDIATE. Both are gone; these tests hold that line against a live SQL Server:
    //   - UseChangeFeedSqlMigrationsAsync leaves sys.databases.owner_sid alone (read back through SUSER_SNAME) on a
    //     database deliberately owned by a NON-'sa' login, and the change feed still delivers notifications from a
    //     trigger running WITH EXECUTE AS OWNER under that owner.
    //   - The broker-enable branch COMPLETES instead of waiting indefinitely while a second session holds an open
    //     transaction on the target database.
    //
    // Gated by [RequiresDockerFact]. Each test owns a DISTINCT row DTO, table and dedicated database, and creates
    // that database with the broker explicitly DISABLED: object names the migration derives come from
    // typeof(TRow).Name, and the branch under test only runs when is_broker_enabled starts at 0.
    [Trait("Category", "Integration")]
    [Collection(SqlChangeFeedCollection.Name)]
    public class ChangeFeedDatabaseOwnershipTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private const string OwnershipDatabaseName = "chatter_scf_it_ownership";
        private const string OwnershipTableName = "ChangeFeedOwnership";

        // The non-'sa' login the ownership database is handed to before the migration runs.
        private const string OwnershipLoginName = "chatter_scf_owner";

        private const string CompetingDatabaseName = "chatter_scf_it_competing";
        private const string CompetingTableName = "ChangeFeedCompetingSession";

        // A table the competing session writes to, kept separate from the watched table so the open transaction
        // cannot itself perturb the change feed under test.
        private const string CompetingBlockerTableName = "ChangeFeedCompetingBlocker";

        private readonly SqlChangeFeedFixture _fixture;

        public ChangeFeedDatabaseOwnershipTests(SqlChangeFeedFixture fixture)
            => _fixture = fixture;

        public sealed class OwnershipRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        public sealed class CompetingSessionRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        [RequiresDockerFact]
        public async Task MigrationLeavesTheDatabaseOwnerUnchangedAndStillDeliversNotifications()
        {
            var masterConnectionString = _fixture.GetMasterConnectionString();
            var appConnectionString = _fixture.GetConnectionStringForDatabase(OwnershipDatabaseName);

            await ChangeFeedTableProvisioning.DropDatabaseAsync(masterConnectionString, OwnershipDatabaseName, CancellationToken.None);
            await ChangeFeedTableProvisioning.CreateDatabaseAsync(masterConnectionString, OwnershipDatabaseName, CancellationToken.None);
            // A freshly created database inherits its broker state from 'model' (broker-enabled on the test image),
            // so explicitly disable it: the removed ownership transfer sat on the ENABLE_BROKER branch, which runs
            // only when the broker starts disabled.
            await ChangeFeedTableProvisioning.DisableBrokerAsync(masterConnectionString, OwnershipDatabaseName, CancellationToken.None);
            await ChangeFeedDatabaseInspection.AssignDatabaseOwnerAsync(masterConnectionString, OwnershipDatabaseName, OwnershipLoginName, CancellationToken.None);

            try
            {
                // Captured, never assumed: the image's default database owner is an implementation detail.
                var ownerBeforeMigration = await ChangeFeedDatabaseInspection.GetDatabaseOwnerAsync(masterConnectionString, OwnershipDatabaseName, CancellationToken.None);
                ownerBeforeMigration.Should().NotBeNullOrEmpty("the database must have a resolvable owner before the migration runs");
                ownerBeforeMigration.Should().NotBe("sa", "the database must start under a non-'sa' owner or an unchanged-ownership assertion passes vacuously");

                await ChangeFeedTableProvisioning.CreateTableAsync(appConnectionString, OwnershipTableName, CancellationToken.None);

                var harness = ChatterChangeFeedHarness<OwnershipRow>.Build(
                    appConnectionString,
                    OwnershipDatabaseName,
                    OwnershipTableName);
                try
                {
                    await harness.RunMigrationsAsync();

                    (await ChangeFeedTableProvisioning.IsBrokerEnabledAsync(masterConnectionString, OwnershipDatabaseName, CancellationToken.None))
                        .Should().BeTrue("the ENABLE_BROKER branch that used to carry the ownership transfer must actually have run");

                    var ownerAfterMigration = await ChangeFeedDatabaseInspection.GetDatabaseOwnerAsync(masterConnectionString, OwnershipDatabaseName, CancellationToken.None);
                    ownerAfterMigration.Should().Be(ownerBeforeMigration, "installing the change feed must not take ownership of the consumer's database");

                    await harness.StartAsync();

                    await ChangeFeedTableProvisioning.InsertRowAsync(appConnectionString, OwnershipTableName, 1, "owner-preserved", "value", CancellationToken.None);

                    var inserted = await harness.Signals.WaitForHandledAsync<RowInsertedEvent<OwnershipRow>>(HandlerWait);
                    inserted.Inserted.Should().NotBeNull("the change feed must still deliver notifications without owning the database");
                    inserted.Inserted.Id.Should().Be(1);
                    inserted.Inserted.Name.Should().Be("owner-preserved");
                }
                finally
                {
                    await harness.DisposeAsync();
                }
            }
            finally
            {
                await ChangeFeedTableProvisioning.DropDatabaseAsync(masterConnectionString, OwnershipDatabaseName, CancellationToken.None);
            }
        }

        [RequiresDockerFact]
        public async Task MigrationEnablesTheBrokerWhileAnotherSessionHoldsAnOpenTransaction()
        {
            var masterConnectionString = _fixture.GetMasterConnectionString();
            var appConnectionString = _fixture.GetConnectionStringForDatabase(CompetingDatabaseName);

            await ChangeFeedTableProvisioning.DropDatabaseAsync(masterConnectionString, CompetingDatabaseName, CancellationToken.None);
            await ChangeFeedTableProvisioning.CreateDatabaseAsync(masterConnectionString, CompetingDatabaseName, CancellationToken.None);
            await ChangeFeedTableProvisioning.DisableBrokerAsync(masterConnectionString, CompetingDatabaseName, CancellationToken.None);

            try
            {
                await ChangeFeedTableProvisioning.CreateTableAsync(appConnectionString, CompetingTableName, CancellationToken.None);
                await ChangeFeedTableProvisioning.CreateTableAsync(appConnectionString, CompetingBlockerTableName, CancellationToken.None);

                // Disposed at the end of this block whatever happens, so a failed assertion still releases the
                // session even though WITH ROLLBACK IMMEDIATE is expected to have terminated it already.
                await using var competingSession = await ChangeFeedDatabaseInspection.OpenSessionHoldingAnOpenTransactionAsync(
                    appConnectionString, CompetingBlockerTableName, CancellationToken.None);

                var harness = ChatterChangeFeedHarness<CompetingSessionRow>.Build(
                    appConnectionString,
                    CompetingDatabaseName,
                    CompetingTableName);
                try
                {
                    // RunMigrationsAsync is bounded and surfaces its ceiling as TimeoutException, so a broker-enable
                    // that waits on the competing session fails this test instead of hanging the suite.
                    await harness.RunMigrationsAsync();

                    (await ChangeFeedTableProvisioning.IsBrokerEnabledAsync(masterConnectionString, CompetingDatabaseName, CancellationToken.None))
                        .Should().BeTrue("the broker-enable must complete rather than wait for the competing session to close");

                    await harness.StartAsync();

                    await ChangeFeedTableProvisioning.InsertRowAsync(appConnectionString, CompetingTableName, 1, "competing-session", "value", CancellationToken.None);

                    var inserted = await harness.Signals.WaitForHandledAsync<RowInsertedEvent<CompetingSessionRow>>(HandlerWait);
                    inserted.Inserted.Should().NotBeNull("the change feed must deliver notifications after a broker-enable that terminated a competing session");
                    inserted.Inserted.Id.Should().Be(1);
                    inserted.Inserted.Name.Should().Be("competing-session");
                }
                finally
                {
                    await harness.DisposeAsync();
                }
            }
            finally
            {
                await ChangeFeedTableProvisioning.DropDatabaseAsync(masterConnectionString, CompetingDatabaseName, CancellationToken.None);
            }
        }
    }
}
