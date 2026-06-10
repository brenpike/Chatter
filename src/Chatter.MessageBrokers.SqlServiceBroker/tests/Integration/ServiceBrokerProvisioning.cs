using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Service Broker provisioning + teardown helper for the SSB integration harness (STEP-003).
    //
    // The production receiver (SqlServiceBrokerReceiver) and sender (SqlServiceBrokerSender) HARD-REFERENCE
    // the Chatter message type "//Chatter/BrokeredMessage" (ServicesMessageTypes.ChatterBrokeredMessageType)
    // and the contract "//Chatter" (ServicesMessageTypes.ChatterServiceContract) for the Chatter-brokered
    // path, so this harness provisions EXACTLY those names. Everything else (DB, service, queue names) is
    // harness-chosen but exposed as reusable constants so the STEP-004 fixture wires the same identifiers into
    // ReceiverOptions.MessageReceiverPath / DeadLetterQueuePath and the stamped SSBMessageContext headers.
    //
    // All CREATE/DROP statements are idempotent (guarded against sys.* catalog views) so re-runs never throw.
    // The initial connect + ENABLE_BROKER is wrapped in a SHORT bounded retry to absorb container-readiness
    // races; it never loops unbounded.
    internal static class ServiceBrokerProvisioning
    {
        // Dedicated application database the harness provisions Service Broker objects in. Kept distinct from
        // any database the SQL container ships with so teardown can drop it wholesale.
        public const string DatabaseName = "chatter_ssb_it";

        // Production-pinned Service Broker object names. The receiver only accepts (and the sender only stamps)
        // these exact strings on the Chatter-brokered path, so they MUST match ServicesMessageTypes.
        public const string MessageTypeName = "//Chatter/BrokeredMessage";
        public const string ContractName = "//Chatter";

        // Harness-chosen SHARED Service Broker objects. The initiator service+queue are the SEND side: the sender
        // begins every dialog FROM this one initiator regardless of which test class is running, so they stay
        // shared (and the message type + contract above stay shared) — only the RECEIVE side is partitioned.
        public const string InitiatorServiceName = "chatter_ssb_it_initiator_service";
        public const string InitiatorQueueName = "chatter_ssb_it_initiator_queue";

        // An immutable per-test-class set of RECEIVE-side Service Broker objects. SSB queues are not partitioned by
        // message type, so a stale message left by a failed prior scenario would otherwise be RECEIVEd by a later
        // test sharing the same target queue. Each test class gets its OWN target queue+service and deadletter
        // queue+service (all ON the shared //Chatter contract) so cross-test poisoning is impossible.
        public readonly record struct ObjectSet(
            string TargetQueueName,
            string TargetServiceName,
            string DeadLetterQueueName,
            string DeadLetterServiceName)
        {
            // Bracket-quoted target queue identifier suitable for direct interpolation into the receiver's
            // "FROM {queue}" RECEIVE statement. The STEP-004 fixture sets ReceiverOptions.MessageReceiverPath to
            // this value for the owning test class.
            public string TargetQueuePathBracketed => "[" + TargetQueueName + "]";
        }

        // Mints a per-class object set from a suffix, e.g. "roundtrip" yields chatter_ssb_it_target_queue_roundtrip
        // / _target_service_roundtrip / _deadletter_queue_roundtrip / _deadletter_service_roundtrip.
        private static ObjectSet CreateSet(string suffix)
            => new ObjectSet(
                TargetQueueName: $"chatter_ssb_it_target_queue_{suffix}",
                TargetServiceName: $"chatter_ssb_it_target_service_{suffix}",
                DeadLetterQueueName: $"chatter_ssb_it_deadletter_queue_{suffix}",
                DeadLetterServiceName: $"chatter_ssb_it_deadletter_service_{suffix}");

        // The four well-known per-test-class object sets, one per integration test class.
        public static readonly ObjectSet RoundTripSet = CreateSet("roundtrip");
        public static readonly ObjectSet NackSet = CreateSet("nack");
        public static readonly ObjectSet DeadLetterSet = CreateSet("deadletter");
        public static readonly ObjectSet DialogSet = CreateSet("dialog");

        // Every object set, driving BOTH provisioning and teardown so the two loops can never diverge and leak.
        public static readonly IReadOnlyList<ObjectSet> AllSets = new[]
        {
            RoundTripSet,
            NackSet,
            DeadLetterSet,
            DialogSet,
        };

        // Bounded readiness retry. A freshly started SQL Server container can refuse connections or report the
        // broker not-yet-enabled for a brief window; these caps keep the retry short and finite.
        private const int MaxConnectAttempts = 10;
        private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);

        // Creates the dedicated database, enables Service Broker on it, and idempotently provisions the message
        // type, contract, queues, and services. Safe to call repeatedly. The connect + database/broker setup is
        // retried a bounded number of times to absorb container-readiness races; provisioning DDL is not retried
        // because by then the server is known-reachable.
        public static async Task SetupAsync(string masterConnectionString, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(masterConnectionString))
            {
                throw new ArgumentException("A connection string to the 'master' database is required.", nameof(masterConnectionString));
            }

            await EnsureDatabaseAndBrokerAsync(masterConnectionString, cancellationToken).ConfigureAwait(false);

            var appConnectionString = BuildAppConnectionString(masterConnectionString);
            await using var connection = new SqlConnection(appConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Shared objects provisioned ONCE: message type, contract, and the single initiator queue+service.
            await ProvisionMessageTypeAsync(connection, cancellationToken).ConfigureAwait(false);
            await ProvisionContractAsync(connection, cancellationToken).ConfigureAwait(false);
            await ProvisionQueueAsync(connection, InitiatorQueueName, cancellationToken).ConfigureAwait(false);
            await ProvisionServiceAsync(connection, InitiatorServiceName, InitiatorQueueName, cancellationToken).ConfigureAwait(false);

            // Per-class RECEIVE-side objects: each set's target queue+service and deadletter queue+service, all ON
            // the shared contract. Driven by AllSets so teardown drops exactly what provisioning created.
            foreach (var set in AllSets)
            {
                await ProvisionQueueAsync(connection, set.TargetQueueName, cancellationToken).ConfigureAwait(false);
                await ProvisionQueueAsync(connection, set.DeadLetterQueueName, cancellationToken).ConfigureAwait(false);

                await ProvisionServiceAsync(connection, set.TargetServiceName, set.TargetQueueName, cancellationToken).ConfigureAwait(false);
                await ProvisionServiceAsync(connection, set.DeadLetterServiceName, set.DeadLetterQueueName, cancellationToken).ConfigureAwait(false);
            }
        }

        // Drops every provisioned Service Broker object (services, queues, contract, message type) and the
        // dedicated database. Idempotent: each drop is guarded so missing objects never throw. The database drop
        // forces SINGLE_USER WITH ROLLBACK IMMEDIATE first so lingering harness connections cannot block it.
        public static async Task TeardownAsync(string masterConnectionString, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(masterConnectionString))
            {
                throw new ArgumentException("A connection string to the 'master' database is required.", nameof(masterConnectionString));
            }

            if (!await DatabaseExistsAsync(masterConnectionString, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var appConnectionString = BuildAppConnectionString(masterConnectionString);

            try
            {
                await using var connection = new SqlConnection(appConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                // Per-class objects first: drop each set's services then its queues (services reference queues so
                // services must go first). Driven by the SAME AllSets enumerable provisioning used, so nothing leaks.
                foreach (var set in AllSets)
                {
                    await DropServiceAsync(connection, set.TargetServiceName, cancellationToken).ConfigureAwait(false);
                    await DropServiceAsync(connection, set.DeadLetterServiceName, cancellationToken).ConfigureAwait(false);

                    await DropQueueAsync(connection, set.TargetQueueName, cancellationToken).ConfigureAwait(false);
                    await DropQueueAsync(connection, set.DeadLetterQueueName, cancellationToken).ConfigureAwait(false);
                }

                // Shared objects last, in reverse dependency order: initiator service+queue, then contract+type.
                await DropServiceAsync(connection, InitiatorServiceName, cancellationToken).ConfigureAwait(false);
                await DropQueueAsync(connection, InitiatorQueueName, cancellationToken).ConfigureAwait(false);

                await DropContractAsync(connection, cancellationToken).ConfigureAwait(false);
                await DropMessageTypeAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // The database is about to be dropped wholesale; an object-level drop failure (e.g. the broker
                // tore objects down already) must not mask teardown. The DROP DATABASE below is authoritative.
            }

            await DropDatabaseAsync(masterConnectionString, cancellationToken).ConfigureAwait(false);
        }

        // Connects to 'master', creates the app database if absent, and enables Service Broker on it. Wrapped in
        // a bounded retry so a not-yet-ready container surfaces as a retry rather than a hard failure.
        private static async Task EnsureDatabaseAndBrokerAsync(string masterConnectionString, CancellationToken cancellationToken)
        {
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    await using var connection = new SqlConnection(masterConnectionString);
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                    await ExecuteNonQueryAsync(connection,
                        $"IF DB_ID('{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];",
                        cancellationToken).ConfigureAwait(false);

                    // ROLLBACK IMMEDIATE evicts any other session holding the database so the broker can be
                    // enabled deterministically. Guarded so an already-enabled broker is left untouched.
                    await ExecuteNonQueryAsync(connection,
                        "IF NOT EXISTS (SELECT 1 FROM sys.databases " +
                        $"WHERE name = '{DatabaseName}' AND is_broker_enabled = 1) " +
                        $"ALTER DATABASE [{DatabaseName}] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;",
                        cancellationToken).ConfigureAwait(false);

                    return;
                }
                catch (SqlException) when (attempt < MaxConnectAttempts)
                {
                    await Task.Delay(ConnectRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static async Task ProvisionMessageTypeAsync(SqlConnection connection, CancellationToken cancellationToken)
            => await ExecuteNonQueryAsync(connection,
                "IF NOT EXISTS (SELECT 1 FROM sys.service_message_types WHERE name = @name) " +
                $"CREATE MESSAGE TYPE [{MessageTypeName}] VALIDATION = NONE;",
                cancellationToken,
                ("@name", MessageTypeName)).ConfigureAwait(false);

        private static async Task ProvisionContractAsync(SqlConnection connection, CancellationToken cancellationToken)
            => await ExecuteNonQueryAsync(connection,
                "IF NOT EXISTS (SELECT 1 FROM sys.service_contracts WHERE name = @name) " +
                $"CREATE CONTRACT [{ContractName}] ([{MessageTypeName}] SENT BY ANY);",
                cancellationToken,
                ("@name", ContractName)).ConfigureAwait(false);

        private static async Task ProvisionQueueAsync(SqlConnection connection, string queueName, CancellationToken cancellationToken)
            => await ExecuteNonQueryAsync(connection,
                "IF NOT EXISTS (SELECT 1 FROM sys.service_queues WHERE name = @name) " +
                $"CREATE QUEUE [{queueName}];",
                cancellationToken,
                ("@name", queueName)).ConfigureAwait(false);

        private static async Task ProvisionServiceAsync(SqlConnection connection, string serviceName, string queueName, CancellationToken cancellationToken)
            => await ExecuteNonQueryAsync(connection,
                "IF NOT EXISTS (SELECT 1 FROM sys.services WHERE name = @name) " +
                $"CREATE SERVICE [{serviceName}] ON QUEUE [{queueName}] ([{ContractName}]);",
                cancellationToken,
                ("@name", serviceName)).ConfigureAwait(false);

        private static async Task DropServiceAsync(SqlConnection connection, string serviceName, CancellationToken cancellationToken)
            => await ExecuteNonQueryAsync(connection,
                "IF EXISTS (SELECT 1 FROM sys.services WHERE name = @name) " +
                $"DROP SERVICE [{serviceName}];",
                cancellationToken,
                ("@name", serviceName)).ConfigureAwait(false);

        private static async Task DropQueueAsync(SqlConnection connection, string queueName, CancellationToken cancellationToken)
            => await ExecuteNonQueryAsync(connection,
                "IF EXISTS (SELECT 1 FROM sys.service_queues WHERE name = @name) " +
                $"DROP QUEUE [{queueName}];",
                cancellationToken,
                ("@name", queueName)).ConfigureAwait(false);

        private static async Task DropContractAsync(SqlConnection connection, CancellationToken cancellationToken)
            => await ExecuteNonQueryAsync(connection,
                "IF EXISTS (SELECT 1 FROM sys.service_contracts WHERE name = @name) " +
                $"DROP CONTRACT [{ContractName}];",
                cancellationToken,
                ("@name", ContractName)).ConfigureAwait(false);

        private static async Task DropMessageTypeAsync(SqlConnection connection, CancellationToken cancellationToken)
            => await ExecuteNonQueryAsync(connection,
                "IF EXISTS (SELECT 1 FROM sys.service_message_types WHERE name = @name) " +
                $"DROP MESSAGE TYPE [{MessageTypeName}];",
                cancellationToken,
                ("@name", MessageTypeName)).ConfigureAwait(false);

        private static async Task<bool> DatabaseExistsAsync(string masterConnectionString, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT DB_ID('{DatabaseName}');";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return result != null && result != DBNull.Value;
        }

        private static async Task DropDatabaseAsync(string masterConnectionString, CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(connection,
                $"IF DB_ID('{DatabaseName}') IS NOT NULL " +
                "BEGIN " +
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                $"DROP DATABASE [{DatabaseName}]; " +
                "END;",
                cancellationToken).ConfigureAwait(false);
        }

        // Returns the supplied master connection string repointed at the app database, preserving every other
        // setting (credentials, encryption, trust flags) the harness configured for the container.
        private static string BuildAppConnectionString(string masterConnectionString)
        {
            var builder = new SqlConnectionStringBuilder(masterConnectionString)
            {
                InitialCatalog = DatabaseName,
            };
            return builder.ConnectionString;
        }

        private static async Task ExecuteNonQueryAsync(SqlConnection connection, string commandText, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.Add(new SqlParameter(name, value));
            }
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
