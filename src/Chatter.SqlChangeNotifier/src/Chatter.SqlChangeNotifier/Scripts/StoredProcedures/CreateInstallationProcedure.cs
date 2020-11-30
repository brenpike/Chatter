using Chatter.SqlChangeNotifier.Scripts.Misc;
using Chatter.SqlChangeNotifier.Scripts.ServiceBroker;
using Chatter.SqlChangeNotifier.Scripts.Triggers;
using System;

namespace Chatter.SqlChangeNotifier.Scripts.StoredProcedures
{
    /// <summary>
    /// Creates the stored procedure that will create necessary database objects needed for notifications
    /// </summary>
    public class CreateInstallationProcedure : ExecutableSqlScript
    {
        private readonly PermissionInfoDisplayScript _getPermissionsInfo;
        private readonly string _databaseName;
        private readonly string _setupProcedureName;
        private readonly InstallAndConfigureSqlServiceBroker _serviceBrokerConfigScript;
        private readonly CreateNotificationTrigger _notificationTriggerConfigScript;
        private readonly CheckIfNotificationTriggerExists _notificationTriggerCheckScript;
        private readonly string _tableName;
        private readonly string _schemaName;

        /// <summary>
        /// Creates the stored procedure that will create necessary database objects needed for notifications
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        /// <param name="getPermissionsInfo">The script used to display required executing permission information</param>
        /// <param name="databaseName">The database where the install stored proc will be created</param>
        /// <param name="setupProcedureName">The name of the stored procedure to create</param>
        /// <param name="serviceBrokerConfigScript">The script which defines all SQL Service Broker related objects</param>
        /// <param name="notificationTriggerConfigScript">The script which will create the trigger responsible for writing to the QUEUE when the target <paramref name="tableName"/> changes</param>
        /// <param name="notificationTriggerCheckScript">The script that will check for the notification triggers existence</param>
        /// <param name="tableName">The target table which will be monitored for changes</param>
        /// <param name="schemaName">The schema to use for the various objects to be created</param>
        public CreateInstallationProcedure(string connectionString,
                                           PermissionInfoDisplayScript getPermissionsInfo,
                                           string databaseName,
                                           string setupProcedureName,
                                           InstallAndConfigureSqlServiceBroker serviceBrokerConfigScript,
                                           CreateNotificationTrigger notificationTriggerConfigScript,
                                           CheckIfNotificationTriggerExists notificationTriggerCheckScript,
                                           string tableName,
                                           string schemaName)
            : base(connectionString)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException($"'{nameof(databaseName)}' cannot be null or whitespace", nameof(databaseName));
            }

            if (string.IsNullOrWhiteSpace(setupProcedureName))
            {
                throw new ArgumentException($"'{nameof(setupProcedureName)}' cannot be null or whitespace", nameof(setupProcedureName));
            }

            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException($"'{nameof(tableName)}' cannot be null or whitespace", nameof(tableName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            _getPermissionsInfo = getPermissionsInfo ?? throw new ArgumentNullException(nameof(getPermissionsInfo));
            _databaseName = databaseName;
            _setupProcedureName = setupProcedureName;
            _serviceBrokerConfigScript = serviceBrokerConfigScript ?? throw new ArgumentNullException(nameof(serviceBrokerConfigScript));
            _notificationTriggerConfigScript = notificationTriggerConfigScript ?? throw new ArgumentNullException(nameof(notificationTriggerConfigScript));
            _notificationTriggerCheckScript = notificationTriggerCheckScript ?? throw new ArgumentNullException(nameof(notificationTriggerCheckScript));
            _tableName = tableName;
            _schemaName = schemaName;
        }

        public override string ToString()
        {
            return string.Format(@"
                USE [{0}]
                " + _getPermissionsInfo.ToString() + @"
                IF OBJECT_ID ('{6}.{1}', 'P') IS NULL
                BEGIN
                    EXEC ('
                        CREATE PROCEDURE {6}.{1}
                        AS
                        BEGIN
                            -- Service Broker configuration statement.
                            {2}

                            -- Notification Trigger check statement.
                            {4}

                            -- Notification Trigger configuration statement.
                            DECLARE @triggerStatement NVARCHAR(MAX)
                            DECLARE @select NVARCHAR(MAX)
                            DECLARE @sqlInserted NVARCHAR(MAX)
                            DECLARE @sqlDeleted NVARCHAR(MAX)
                            
                            SET @triggerStatement = N''{3}''
                            
                            SET @select = STUFF((SELECT '','' + ''['' + COLUMN_NAME + '']''
                               FROM INFORMATION_SCHEMA.COLUMNS
                               WHERE DATA_TYPE NOT IN  (''text'',''ntext'',''image'',''geometry'',''geography'') AND TABLE_SCHEMA = ''{6}'' AND TABLE_NAME = ''{5}'' AND TABLE_CATALOG = ''{0}''
                               FOR XML PATH ('''')
                               ), 1, 1, '''')

                            SET @sqlInserted =
                                N''SET @InsertedJSON = (SELECT '' + @select + N''
                                                                         FROM INSERTED
                                                                         FOR JSON AUTO, ROOT(''''Inserted''''))''

                            SET @sqlDeleted =
                                N''SET @DeletedJSON = (SELECT '' + @select + N''
                                                                         FROM DELETED
                                                                         FOR JSON AUTO, ROOT(''''Deleted''''))''

                            SET @triggerStatement = REPLACE(@triggerStatement
                                                     , ''%inserted_select_statement%'', @sqlInserted)

                            SET @triggerStatement = REPLACE(@triggerStatement
                                                     , ''%deleted_select_statement%'', @sqlDeleted)

                            EXEC sp_executesql @triggerStatement
                        END
                        ')
                END
            ", _databaseName, _setupProcedureName, _serviceBrokerConfigScript.ToString().Replace("'", "''"), _notificationTriggerConfigScript.ToString().Replace("'", "''''"), _notificationTriggerCheckScript.ToString().Replace("'", "''"), _tableName, _schemaName);
        }
    }
}
