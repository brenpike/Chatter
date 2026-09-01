using System;
using System.Linq;
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
    // Schema-drift proof for the SqlChangeFeed integration suite (#350). The change-feed trigger's SELECT column
    // list is derived from the watched table's columns at install time. When a consumer later drops or renames a
    // watched column in an ordinary schema migration, the installed trigger references a column that no longer
    // exists and raises INSIDE THE CONSUMER'S OWN INSERT/UPDATE/DELETE, aborting their transactions until someone
    // manually uninstalls and reinstalls.
    //
    // The install procedure therefore fingerprints the current column set, embeds the fingerprint in the trigger it
    // creates, and on every later run compares the fingerprint it recomputes from INFORMATION_SCHEMA against the one
    // the installed trigger carries: match -> the trigger is left completely alone; mismatch (or a marker-less
    // trigger installed by an earlier package version) -> it is dropped and recreated from the current column set.
    //
    // BOTH halves are proven here, because a refresh-ALWAYS implementation would pass the drift half alone:
    //   - ChangeFeedRefreshesTheTriggerAfterTheWatchedTableColumnsDrift: install -> notify -> ADD a column -> DROP a
    //     column -> re-run migrations -> DML on the watched table STILL SUCCEEDS and notifications carry the CURRENT
    //     column set.
    //   - ChangeFeedLeavesTheTriggerUntouchedWhenTheWatchedTableColumnsAreUnchanged: a second migration run with no
    //     schema change leaves the trigger's object_id and modify_date untouched.
    //
    // Gated by [RequiresDockerFact]. Each test owns a DISTINCT row DTO + table (object names the migration derives
    // come from typeof(TRow).Name, so a shared DTO across tests would collide).
    [Trait("Category", "Integration")]
    [Collection(SqlChangeFeedCollection.Name)]
    public class ChangeFeedSchemaDriftTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private const string DriftTableName = "ChangeFeedSchemaDrift";
        private const string SteadyTableName = "ChangeFeedSchemaSteady";

        // The column ADDED to the watched table after install; it must appear in the refreshed trigger's payload.
        private const string AddedColumnName = "Extra";

        // The column DROPPED from the watched table after install; it is the one the stale trigger still references,
        // which is what aborts the consumer's DML until the trigger is refreshed.
        private const string DroppedColumnName = "Value";

        private readonly SqlChangeFeedFixture _fixture;

        public ChangeFeedSchemaDriftTests(SqlChangeFeedFixture fixture)
            => _fixture = fixture;

        // Carries a property for the added column AND the dropped one, so a notification emitted after the refresh
        // shows the added column populated and the dropped column null.
        public sealed class SchemaDriftRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
            public string Extra { get; set; }
        }

        public sealed class SchemaSteadyRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        [RequiresDockerFact]
        public async Task ChangeFeedRefreshesTheTriggerAfterTheWatchedTableColumnsDrift()
        {
            var connectionString = _fixture.GetAppConnectionString();
            var triggerName = TriggerNameFor<SchemaDriftRow>();

            await ChangeFeedTableProvisioning.DropTableAsync(connectionString, DriftTableName, CancellationToken.None);
            await ChangeFeedTableProvisioning.CreateTableAsync(connectionString, DriftTableName, CancellationToken.None);

            var harness = ChatterChangeFeedHarness<SchemaDriftRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                DriftTableName);
            try
            {
                await harness.RunMigrationsAsync();
                await harness.StartAsync();

                await ChangeFeedTableProvisioning.InsertRowAsync(connectionString, DriftTableName, 1, "before", "before-value", CancellationToken.None);
                var beforeDrift = await harness.Signals.WaitForHandledAsync<RowInsertedEvent<SchemaDriftRow>>(HandlerWait);
                beforeDrift.Inserted.Value.Should().Be("before-value", "the installed trigger reports the column set it was installed against");
                beforeDrift.Inserted.Extra.Should().BeNull("the added column does not exist yet");

                var installedTriggerId = await ChangeFeedTableProvisioning.GetTriggerObjectIdAsync(connectionString, triggerName, CancellationToken.None);
                installedTriggerId.Should().NotBeNull("the migration must have installed the change-feed trigger");

                // An ordinary consumer schema migration: widen the table and drop a column the trigger references.
                await ChangeFeedTableProvisioning.AddColumnAsync(connectionString, DriftTableName, AddedColumnName, CancellationToken.None);
                await ChangeFeedTableProvisioning.DropColumnAsync(connectionString, DriftTableName, DroppedColumnName, CancellationToken.None);

                // The consumer's own DML is what breaks: the stale trigger still names the dropped column.
                await FluentActions.Awaiting(() => ChangeFeedTableProvisioning.InsertRowWithColumnsAsync(
                        connectionString, DriftTableName, 2, CancellationToken.None, ("Name", "stranded"), (AddedColumnName, "stranded-extra")))
                    .Should().ThrowAsync<SqlException>("a trigger stranded on a dropped column aborts the consumer's own INSERT");

                // Re-running migrations is the repair path: the fingerprint no longer matches, so the trigger is
                // dropped and recreated from the current column set.
                await harness.RunMigrationsAsync();

                var refreshedTriggerId = await ChangeFeedTableProvisioning.GetTriggerObjectIdAsync(connectionString, triggerName, CancellationToken.None);
                refreshedTriggerId.Should().NotBeNull("the refreshed trigger must exist");
                refreshedTriggerId.Should().NotBe(installedTriggerId, "a drifted column set must produce a NEW trigger, not the stale one");

                var refreshedDefinition = await ChangeFeedTableProvisioning.GetTriggerDefinitionAsync(connectionString, triggerName, CancellationToken.None);
                refreshedDefinition.Should().Contain($"[{AddedColumnName}]", "the refreshed trigger must report the added column")
                                   .And.NotContain($"[{DroppedColumnName}]", "the refreshed trigger must not reference the dropped column");

                // The assertion that represents the consumer's pain: their DML succeeds again.
                await ChangeFeedTableProvisioning.InsertRowWithColumnsAsync(
                    connectionString, DriftTableName, 3, CancellationToken.None, ("Name", "after"), (AddedColumnName, "after-extra"));

                await harness.Signals.WaitForInvocationCountAsync<RowInsertedEvent<SchemaDriftRow>>(2, HandlerWait);
                var afterDrift = harness.Signals.GetOrAdd<RowInsertedEvent<SchemaDriftRow>>().Records
                    .FirstOrDefault(e => e.Inserted != null && e.Inserted.Id == 3);
                afterDrift.Should().NotBeNull("the refreshed trigger must keep notifying");
                afterDrift.Inserted.Extra.Should().Be("after-extra", "the notification must carry the added column");
                afterDrift.Inserted.Value.Should().BeNull("the notification must not carry the dropped column");
            }
            finally
            {
                await harness.DisposeAsync();
                await ChangeFeedTableProvisioning.DropTableAsync(connectionString, DriftTableName, CancellationToken.None);
            }
        }

        [RequiresDockerFact]
        public async Task ChangeFeedLeavesTheTriggerUntouchedWhenTheWatchedTableColumnsAreUnchanged()
        {
            var connectionString = _fixture.GetAppConnectionString();
            var triggerName = TriggerNameFor<SchemaSteadyRow>();

            await ChangeFeedTableProvisioning.DropTableAsync(connectionString, SteadyTableName, CancellationToken.None);
            await ChangeFeedTableProvisioning.CreateTableAsync(connectionString, SteadyTableName, CancellationToken.None);

            var harness = ChatterChangeFeedHarness<SchemaSteadyRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                SteadyTableName);
            try
            {
                await harness.RunMigrationsAsync();

                var installedTriggerId = await ChangeFeedTableProvisioning.GetTriggerObjectIdAsync(connectionString, triggerName, CancellationToken.None);
                var installedModifiedDate = await ChangeFeedTableProvisioning.GetTriggerModifiedDateAsync(connectionString, triggerName, CancellationToken.None);
                installedTriggerId.Should().NotBeNull("the migration must have installed the change-feed trigger");

                // A second migration run against an UNCHANGED table: the fingerprint matches, so the install
                // procedure must leave the trigger completely alone rather than dropping and recreating it.
                await harness.RunMigrationsAsync();

                var afterSecondRunTriggerId = await ChangeFeedTableProvisioning.GetTriggerObjectIdAsync(connectionString, triggerName, CancellationToken.None);
                var afterSecondRunModifiedDate = await ChangeFeedTableProvisioning.GetTriggerModifiedDateAsync(connectionString, triggerName, CancellationToken.None);

                afterSecondRunTriggerId.Should().Be(installedTriggerId, "an unchanged column set must not drop and recreate the trigger");
                afterSecondRunModifiedDate.Should().Be(installedModifiedDate, "an unchanged column set must leave the trigger's modify_date untouched");
            }
            finally
            {
                await harness.DisposeAsync();
                await ChangeFeedTableProvisioning.DropTableAsync(connectionString, SteadyTableName, CancellationToken.None);
            }
        }

        // The trigger name the production migration derives from the row type name.
        private static string TriggerNameFor<TRow>()
            => $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{typeof(TRow).Name}";
    }
}
