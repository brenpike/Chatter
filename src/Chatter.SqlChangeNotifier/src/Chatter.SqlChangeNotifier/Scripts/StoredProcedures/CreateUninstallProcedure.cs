using Chatter.SqlChangeNotifier.Scripts.Misc;
using Chatter.SqlChangeNotifier.Scripts.ServiceBroker;
using Chatter.SqlChangeNotifier.Scripts.Triggers;
using System;

namespace Chatter.SqlChangeNotifier.Scripts.StoredProcedures
{
    /// <summary>
    /// Creates the stored procedure that will remove necessary database objects needed for notifications
    /// </summary>
    public class CreateUninstallProcedure : ExecutableSqlScript
    {
        private readonly PermissionInfoDisplayScript _getPermissionsInfo;
        private readonly string _databaseName;
        private readonly string _uninstallProcedureName;
        private readonly DeleteNotificationTrigger _dropNotificationTriggerScript;
        private readonly UninstallSqlServiceBroker _serviceBrokerUninstallScript;
        private readonly string _schemaName;
        private readonly string _installProcedureName;

        /// <summary>
        /// Creates the stored procedure that will remove necessary database objects needed for notifications
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        /// <param name="getPermissionsInfo">The script used to display required executing permission information</param>
        /// <param name="databaseName">The database where the install stored proc will be created</param>
        /// <param name="uninstallProcedureName">The name of the stored procedure to create</param>
        /// <param name="dropNotificationTriggerScript">The script used to remove the notification trigger</param>
        /// <param name="serviceBrokerUninstallScript">The script used to remove all SQL Service Broker related database resources</param>
        /// <param name="schemaName">The schema of the database resources to be removed</param>
        /// <param name="installProcedureName">The name of the installation stored procedure</param>
        public CreateUninstallProcedure(string connectionString,
                                        PermissionInfoDisplayScript getPermissionsInfo,
                                        string databaseName,
                                        string uninstallProcedureName,
                                        DeleteNotificationTrigger dropNotificationTriggerScript,
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

            _getPermissionsInfo = getPermissionsInfo ?? throw new ArgumentNullException(nameof(getPermissionsInfo));
            _databaseName = databaseName;
            _uninstallProcedureName = uninstallProcedureName;
            _dropNotificationTriggerScript = dropNotificationTriggerScript ?? throw new ArgumentNullException(nameof(dropNotificationTriggerScript));
            _serviceBrokerUninstallScript = serviceBrokerUninstallScript ?? throw new ArgumentNullException(nameof(serviceBrokerUninstallScript));
            _schemaName = schemaName;
            _installProcedureName = installProcedureName;
        }

        public override string ToString()
        {
            return string.Format(@"
                USE [{0}]
                " + _getPermissionsInfo.ToString() + @"
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
             _dropNotificationTriggerScript.ToString().Replace("'", "''"),
             _serviceBrokerUninstallScript.ToString().Replace("'", "''"),
             _schemaName,
             _installProcedureName);
        }
    }
}
