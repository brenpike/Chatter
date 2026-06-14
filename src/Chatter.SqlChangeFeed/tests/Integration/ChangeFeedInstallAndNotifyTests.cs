using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // Canonical end-to-end proof for the SqlChangeFeed integration suite (STEP-C5). The SYSTEM UNDER TEST is the
    // REAL production change-feed path: UseChangeFeedSqlMigrations<TRow> installs the trigger + Service Broker
    // objects on a live SQL Server, then INSERT / UPDATE / DELETE statements against the watched table fire the
    // trigger, the ChangeFeedReceiver decomposes the change message, and Chatter dispatches RowInsertedEvent /
    // RowUpdatedEvent / RowDeletedEvent to DI-resolved handlers — every assertion reads the payload the production
    // path materialized from the row change, never a raw SQL / ADO.NET type.
    //
    // Gated by [RequiresDockerFact]: SKIPPED (never failed) when Docker is absent so a plain `dotnet test` stays
    // green; the integration CI lane (`--filter Category=Integration`) runs it for real. Mirrors the SQL Service
    // Broker SsbRoundTripTests.
    [Trait("Category", "Integration")]
    [Collection(SqlChangeFeedCollection.Name)]
    public class ChangeFeedInstallAndNotifyTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        // DISTINCT per-class table. The production migration derives all object names from typeof(TRow).Name, so a
        // shared table/DTO across classes would collide; this class owns InstallNotifyRow + this table exclusively.
        private const string TableName = "ChangeFeedInstallNotify";

        private readonly SqlChangeFeedFixture _fixture;

        public ChangeFeedInstallAndNotifyTests(SqlChangeFeedFixture fixture)
            => _fixture = fixture;

        // DISTINCT per-class row DTO. Properties map by name to the watched table columns (Id/Name/Value) so the
        // trigger's FOR JSON serialization round-trips into this type on the receive path.
        public sealed class InstallNotifyRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        [RequiresDockerFact]
        public async Task InstalledChangeFeedDeliversInsertUpdateAndDeleteEventsWithPayload()
        {
            var connectionString = _fixture.GetAppConnectionString();
            await ChangeFeedTableProvisioning.CreateTableAsync(connectionString, TableName, CancellationToken.None);

            var harness = ChatterChangeFeedHarness<InstallNotifyRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                TableName);
            try
            {
                await harness.RunMigrationsAsync();
                await harness.StartAsync();

                // INSERT -> RowInsertedEvent with the inserted row materialized.
                await ChangeFeedTableProvisioning.InsertRowAsync(connectionString, TableName, 1, "inserted-name", "inserted-value", CancellationToken.None);

                var inserted = await harness.Signals.WaitForHandledAsync<RowInsertedEvent<InstallNotifyRow>>(HandlerWait);
                inserted.Inserted.Should().NotBeNull("the change feed must materialize the inserted row");
                inserted.Inserted.Id.Should().Be(1);
                inserted.Inserted.Name.Should().Be("inserted-name");
                inserted.Inserted.Value.Should().Be("inserted-value");

                // UPDATE -> RowUpdatedEvent carrying both the new and the old row state.
                await ChangeFeedTableProvisioning.UpdateRowAsync(connectionString, TableName, 1, "updated-name", "updated-value", CancellationToken.None);

                var updated = await harness.Signals.WaitForHandledAsync<RowUpdatedEvent<InstallNotifyRow>>(HandlerWait);
                updated.NewValue.Should().NotBeNull("the change feed must materialize the new row state on update");
                updated.NewValue.Id.Should().Be(1);
                updated.NewValue.Name.Should().Be("updated-name");
                updated.NewValue.Value.Should().Be("updated-value");
                updated.OldValue.Should().NotBeNull("the change feed must materialize the old row state on update");
                updated.OldValue.Id.Should().Be(1);
                updated.OldValue.Name.Should().Be("inserted-name");
                updated.OldValue.Value.Should().Be("inserted-value");

                // DELETE -> RowDeletedEvent carrying the deleted row state.
                await ChangeFeedTableProvisioning.DeleteRowAsync(connectionString, TableName, 1, CancellationToken.None);

                var deleted = await harness.Signals.WaitForHandledAsync<RowDeletedEvent<InstallNotifyRow>>(HandlerWait);
                deleted.Deleted.Should().NotBeNull("the change feed must materialize the deleted row");
                deleted.Deleted.Id.Should().Be(1);
                deleted.Deleted.Name.Should().Be("updated-name");
                deleted.Deleted.Value.Should().Be("updated-value");
            }
            finally
            {
                await harness.DisposeAsync();
                await ChangeFeedTableProvisioning.DropTableAsync(connectionString, TableName, CancellationToken.None);
            }
        }
    }
}
