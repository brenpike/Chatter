using Chatter.SqlChangeFeed.Scripts.ServiceBroker;
using Chatter.SqlChangeFeed.Scripts.Triggers;
using System;

namespace Chatter.SqlChangeFeed.Scripts.StoredProcedures
{
    /// <summary>
    /// Creates the stored procedure that will remove necessary database objects needed for the change feed
    /// </summary>
    public class CreateUninstallProcedure : ExecutableSqlScript
    {
        private readonly string _databaseName;
        private readonly string _uninstallProcedureName;
        private readonly DeleteChangeFeedTrigger _dropChangeFeedTriggerScript;
        private readonly UninstallSqlServiceBroker _serviceBrokerUninstallScript;
        private readonly string _schemaName;
        private readonly string _installProcedureName;

        /// <summary>
        /// Creates the stored procedure that will remove necessary database objects needed for the change feed
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        /// <param name="databaseName">The database where the install stored proc will be created</param>
        /// <param name="uninstallProcedureName">The name of the stored procedure to create</param>
        /// <param name="dropChangeFeedTriggerScript">The script used to remove the change feed trigger</param>
        /// <param name="serviceBrokerUninstallScript">The script used to remove all SQL Service Broker related database resources</param>
        /// <param name="schemaName">The schema of the database resources to be removed</param>
        /// <param name="installProcedureName">The name of the installation stored procedure</param>
        public CreateUninstallProcedure(string connectionString,
                                        string databaseName,
                                        string uninstallProcedureName,
                                        DeleteChangeFeedTrigger dropChangeFeedTriggerScript,
                                        UninstallSqlServiceBroker serviceBrokerUninstallScript,
                                        string schemaName,
                                        string installProcedureName)
            : base(connectionString)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException($"'{nameof(databaseName)}' cannot be null or whitespace", nameof(databaseName));
            }

            if (string.IsNullOrWhiteSpace(uninstallProcedureName))
            {
                throw new ArgumentException($"'{nameof(uninstallProcedureName)}' cannot be null or whitespace", nameof(uninstallProcedureName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            if (string.IsNullOrWhiteSpace(installProcedureName))
            {
                throw new ArgumentException($"'{nameof(installProcedureName)}' cannot be null or whitespace", nameof(installProcedureName));
            }

            _databaseName = databaseName;
            _uninstallProcedureName = uninstallProcedureName;
            _dropChangeFeedTriggerScript = dropChangeFeedTriggerScript ?? throw new ArgumentNullException(nameof(dropChangeFeedTriggerScript));
            _serviceBrokerUninstallScript = serviceBrokerUninstallScript ?? throw new ArgumentNullException(nameof(serviceBrokerUninstallScript));
            _schemaName = schemaName;
            _installProcedureName = installProcedureName;
        }

        public override string ToString()
        {
            return string.Format(@"
                USE [{0}]
                IF OBJECT_ID ('{4}.{1}', 'P') IS NULL
                BEGIN
                    EXEC ('
                        CREATE PROCEDURE {4}.{1}
                        AS
                        BEGIN
                            {3}

                            {2}

                            IF OBJECT_ID (''{4}.{5}'', ''P'') IS NOT NULL
                                DROP PROCEDURE {4}.{5}
                            
                            DROP PROCEDURE {4}.{1}
                        END
                        ')
                END
            ",
             _databaseName,
             _uninstallProcedureName,
             _dropChangeFeedTriggerScript.ToString().Replace("'", "''"),
             _serviceBrokerUninstallScript.ToString().Replace("'", "''"),
             _schemaName,
             _installProcedureName);
        }
    }
}
