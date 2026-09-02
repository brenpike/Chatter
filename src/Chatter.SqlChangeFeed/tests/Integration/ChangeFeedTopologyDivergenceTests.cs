using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.Scripts;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Integration
{
    // Topology-divergence proof for the SqlChangeFeed integration suite (#353). A SERVICE carries its binding in
    // sys.services.service_queue_id, a column no name probe can see, so the name guards in the install Change Feed
    // Stored Procedure's Service Broker section skip a service that already exists bound to a queue the current
    // configuration no longer uses. This class runs the service-binding gate against a GENUINELY diverged database:
    // it installs a change feed under one configuration, re-runs the Change Feed Migration under another, and
    // asserts the catalog joins resolve, the refusal names the divergence, nothing was created, the previously
    // installed uninstall Change Feed Stored Procedure survives intact, and the remedy the refusal prescribes
    // actually works end to end.
    //
    // Gated by [RequiresDockerFact]. Each case owns a DISTINCT row DTO + table (object names the Change Feed
    // Migration derives come from typeof(TRow).Name, so a shared DTO across cases would collide).
    [Trait("Category", "Integration")]
    [Collection(SqlChangeFeedCollection.Name)]
    public class ChangeFeedTopologyDivergenceTests
    {
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        private const string QueueRenameTableName = "ChangeFeedQueueRename";
        private const string DeadLetterRenameTableName = "ChangeFeedDeadLetterRename";
        private const string RemedyTableName = "ChangeFeedRemedy";

        // Configured names the SECOND Change Feed Migration of each case supplies. Deliberately unlike any name
        // ChangeFeedObjectNames derives, so a divergence assertion cannot pass by accident on a derived name.
        private const string RenamedQueueName = "ChangeFeedRenamedQueue";
        private const string RenamedDeadLetterServiceName = "ChangeFeedRenamedDeadLetterService";
        private const string RemedyQueueName = "ChangeFeedRemedyQueue";

        private readonly SqlChangeFeedFixture _fixture;

        public ChangeFeedTopologyDivergenceTests(SqlChangeFeedFixture fixture)
            => _fixture = fixture;

        public sealed class QueueRenameRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        public sealed class DeadLetterRenameRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        public sealed class RemedyRow : IMessage
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Value { get; set; }
        }

        [RequiresDockerFact]
        public async Task ChangeFeedMigrationRefusesAConfiguredQueueRenameAndLeavesTheUninstallStoredProcedureIntact()
        {
            var connectionString = _fixture.GetAppConnectionString();
            var rowName = typeof(QueueRenameRow).Name;
            var installedQueueName = $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{rowName}";
            var conversationServiceName = $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{rowName}";
            var uninstallProcedureName = $"{ChatterServiceBrokerConstants.ChatterUninstallChangeFeedPrefix}{rowName}";

            await ChangeFeedTableProvisioning.DropTableAsync(connectionString, QueueRenameTableName, CancellationToken.None);
            await ChangeFeedTableProvisioning.CreateTableAsync(connectionString, QueueRenameTableName, CancellationToken.None);

            var installed = ChatterChangeFeedHarness<QueueRenameRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                QueueRenameTableName);
            try
            {
                await installed.RunMigrationsAsync();

                (await ChangeFeedTableProvisioning.GetQueueBoundToServiceAsync(connectionString, conversationServiceName, CancellationToken.None))
                    .Should().Be(installedQueueName, "the first Change Feed Migration binds the conversation service to the derived queue");

                var uninstallProcedureBeforeRefusal = await ChangeFeedTableProvisioning
                    .GetStoredProcedureDefinitionAsync(connectionString, uninstallProcedureName, CancellationToken.None);
                uninstallProcedureBeforeRefusal.Should()
                    .NotBeNull("the first Change Feed Migration must install the uninstall Change Feed Stored Procedure");

                var refusal = await RunDivergedMigrationExpectingRefusalAsync<QueueRenameRow>(
                    connectionString,
                    QueueRenameTableName,
                    options => options.WithChangeFeedQueueName(RenamedQueueName));

                refusal.Should()
                    .Contain($"[{conversationServiceName}]", "the refusal must name the diverged service")
                    .And.Contain($"[dbo].[{installedQueueName}]", "the refusal must name the queue that service is actually bound to")
                    .And.Contain($"[dbo].[{RenamedQueueName}]", "the refusal must name the queue this configuration expects")
                    .And.Contain(ChatterServiceBrokerConstants.ChatterUninstallChangeFeedPrefix, "the refusal must prescribe the remedy");

                (await ChangeFeedTableProvisioning.QueueExistsAsync(connectionString, RenamedQueueName, CancellationToken.None))
                    .Should().BeFalse("the gate is non-destructive, so a refused Change Feed Migration creates no queue");
                (await ChangeFeedTableProvisioning.GetQueueBoundToServiceAsync(connectionString, conversationServiceName, CancellationToken.None))
                    .Should().Be(installedQueueName, "a refused Change Feed Migration must not rebind the installed service");

                // The reorder proof: the uninstall Change Feed Stored Procedure is regenerated only after the
                // install procedure has executed successfully, so a refusal leaves the consumer's only handle on
                // the already-installed objects byte-for-byte intact.
                var uninstallProcedureAfterRefusal = await ChangeFeedTableProvisioning
                    .GetStoredProcedureDefinitionAsync(connectionString, uninstallProcedureName, CancellationToken.None);
                uninstallProcedureAfterRefusal.Should()
                    .Be(uninstallProcedureBeforeRefusal, "a refused Change Feed Migration must not overwrite the installed uninstall Change Feed Stored Procedure");
            }
            finally
            {
                await installed.UninstallAsync();
                await installed.DisposeAsync();
                await ChangeFeedTableProvisioning.DropTableAsync(connectionString, QueueRenameTableName, CancellationToken.None);
            }
        }

        [RequiresDockerFact]
        public async Task ChangeFeedMigrationRefusesAConfiguredDeadLetterServiceRenameAndLeavesTheDeadLetterQueueDroppable()
        {
            var connectionString = _fixture.GetAppConnectionString();
            var rowName = typeof(DeadLetterRenameRow).Name;
            var deadLetterQueueName = $"{ChatterServiceBrokerConstants.ChatterDeadLetterQueuePrefix}{rowName}";
            var installedDeadLetterServiceName = $"{ChatterServiceBrokerConstants.ChatterDeadLetterServicePrefix}{rowName}";

            await ChangeFeedTableProvisioning.DropTableAsync(connectionString, DeadLetterRenameTableName, CancellationToken.None);
            await ChangeFeedTableProvisioning.CreateTableAsync(connectionString, DeadLetterRenameTableName, CancellationToken.None);

            var installed = ChatterChangeFeedHarness<DeadLetterRenameRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                DeadLetterRenameTableName);
            try
            {
                await installed.RunMigrationsAsync();

                (await ChangeFeedTableProvisioning.GetQueueBoundToServiceAsync(connectionString, installedDeadLetterServiceName, CancellationToken.None))
                    .Should().Be(deadLetterQueueName, "the first Change Feed Migration binds the derived dead letter service to the dead letter queue");

                var refusal = await RunDivergedMigrationExpectingRefusalAsync<DeadLetterRenameRow>(
                    connectionString,
                    DeadLetterRenameTableName,
                    options => options.WithChangeFeedDeadLetterServiceName(RenamedDeadLetterServiceName));

                refusal.Should()
                    .Contain(installedDeadLetterServiceName, "the refusal must name the superseded dead letter service")
                    .And.Contain($"[dbo].[{deadLetterQueueName}]", "the refusal must name the dead letter queue that service is bound to")
                    .And.Contain($"[{RenamedDeadLetterServiceName}]", "the refusal must name the dead letter service this configuration expects")
                    .And.Contain(ChatterServiceBrokerConstants.ChatterUninstallChangeFeedPrefix, "the refusal must prescribe the remedy");

                (await ChangeFeedTableProvisioning.ServiceExistsAsync(connectionString, RenamedDeadLetterServiceName, CancellationToken.None))
                    .Should().BeFalse("the gate is non-destructive, so a refused Change Feed Migration creates no dead letter service");

                // No superseded service was left bound in a way that blocks teardown: the preserved uninstall
                // Change Feed Stored Procedure still knows the service it created, so it drops that service and
                // then the dead letter queue it occupied.
                await installed.UninstallAsync();

                (await ChangeFeedTableProvisioning.ServiceExistsAsync(connectionString, installedDeadLetterServiceName, CancellationToken.None))
                    .Should().BeFalse("the preserved uninstall Change Feed Stored Procedure must drop the dead letter service");
                (await ChangeFeedTableProvisioning.QueueExistsAsync(connectionString, deadLetterQueueName, CancellationToken.None))
                    .Should().BeFalse("the dead letter queue must remain droppable after a refused Change Feed Migration");
            }
            finally
            {
                await installed.UninstallAsync();
                await installed.DisposeAsync();
                await ChangeFeedTableProvisioning.DropTableAsync(connectionString, DeadLetterRenameTableName, CancellationToken.None);
            }
        }

        [RequiresDockerFact]
        public async Task ChangeFeedMigrationAcceptsAConfiguredQueueRenameAfterThePrescribedUninstallAndDeliversChanges()
        {
            var connectionString = _fixture.GetAppConnectionString();
            var rowName = typeof(RemedyRow).Name;
            var derivedQueueName = $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{rowName}";
            var conversationServiceName = $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{rowName}";

            await ChangeFeedTableProvisioning.DropTableAsync(connectionString, RemedyTableName, CancellationToken.None);
            await ChangeFeedTableProvisioning.CreateTableAsync(connectionString, RemedyTableName, CancellationToken.None);

            var installed = ChatterChangeFeedHarness<RemedyRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                RemedyTableName);
            try
            {
                await installed.RunMigrationsAsync();

                (await ChangeFeedTableProvisioning.GetQueueBoundToServiceAsync(connectionString, conversationServiceName, CancellationToken.None))
                    .Should().Be(derivedQueueName, "the first Change Feed Migration binds the conversation service to the derived queue");

                // The remedy the refusal prescribes: run the installed uninstall Change Feed Stored Procedure.
                await installed.UninstallAsync();
            }
            finally
            {
                await installed.DisposeAsync();
            }

            var renamed = ChatterChangeFeedHarness<RemedyRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                RemedyTableName,
                options => options.WithChangeFeedQueueName(RemedyQueueName));
            try
            {
                await renamed.RunMigrationsAsync();

                (await ChangeFeedTableProvisioning.GetQueueBoundToServiceAsync(connectionString, conversationServiceName, CancellationToken.None))
                    .Should().Be(RemedyQueueName, "the prescribed remedy must let the Change Feed Migration bind the conversation service to the configured queue");

                await renamed.StartAsync();
                await ChangeFeedTableProvisioning.InsertRowAsync(connectionString, RemedyTableName, 1, "remedy-name", "remedy-value", CancellationToken.None);

                var inserted = await renamed.Signals.WaitForHandledAsync<RowInsertedEvent<RemedyRow>>(HandlerWait);
                inserted.Inserted.Should().NotBeNull("the rebound change feed must materialize the inserted row");
                inserted.Inserted.Id.Should().Be(1);
                inserted.Inserted.Name.Should().Be("remedy-name");
                inserted.Inserted.Value.Should().Be("remedy-value");
            }
            finally
            {
                // Teardown drops the watched table only and leaves the reinstalled objects in place, matching the
                // other end-to-end classes that run the receiver pump. Running the uninstall Change Feed Stored
                // Procedure here instead BLOCKS: a pump that has been cancelled and drained still holds its
                // receive session on the queue, so DROP QUEUE waits out the command timeout. The fixture drops the
                // whole database on collection teardown, and every object name in this case is unique to it.
                await renamed.DisposeAsync();
                await ChangeFeedTableProvisioning.DropTableAsync(connectionString, RemedyTableName, CancellationToken.None);
            }
        }

        // Re-runs the PRODUCTION Change Feed Migration against the already-installed database under a DIVERGED
        // configuration, expecting the install Change Feed Stored Procedure's service-binding gate to refuse it, and
        // returns the raised message for divergence assertions.
        private async Task<string> RunDivergedMigrationExpectingRefusalAsync<TRow>(
            string connectionString,
            string tableName,
            Action<SqlChangeFeedOptionsBuilder> diverge)
            where TRow : class, IMessage, new()
        {
            var diverged = ChatterChangeFeedHarness<TRow>.Build(
                connectionString,
                SqlChangeFeedFixture.DatabaseName,
                tableName,
                diverge);
            try
            {
                var refusal = await FluentActions.Awaiting(() => diverged.RunMigrationsAsync())
                    .Should().ThrowAsync<SqlException>("the install Change Feed Stored Procedure must raise a service-binding error");

                return refusal.Which.Message;
            }
            finally
            {
                await diverged.DisposeAsync();
            }
        }
    }
}
