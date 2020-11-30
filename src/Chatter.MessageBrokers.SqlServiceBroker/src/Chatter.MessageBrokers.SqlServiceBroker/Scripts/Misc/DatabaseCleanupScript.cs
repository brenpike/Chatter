using System;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts.Misc
{
    /// <summary>
    /// Uninstalls and removes all database entities that were created upon installation of Chatter Sql Service Broker Receivers.
    /// </summary>
    public class DatabaseCleanupScript : ExecutableSqlScript
    {
        private readonly string _databaseName;
        private readonly string _installNotificationsStoredProcPrefix;
        private readonly string _uninstallNotificationsStoredProcPrefix;

        /// <summary>
        /// Uninstalls and removes all database entities that were created upon installation of Chatter Sql Service Broker Receivers.
        /// </summary>
        /// <param name="connectionString">The sql connection string</param>
        /// <param name="databaseName">The name of the database to clean up</param>
        /// <param name="installNotificationsStoredProcPrefix">The name of the notification installation stored proc to remove</param>
        /// <param name="uninstallNotificationsStoredProcPrefix">The name of the notification uninstallation stored proc to execute and then remove.</param>
        public DatabaseCleanupScript(string connectionString,
                                     string databaseName,
                                     string installNotificationsStoredProcPrefix = ChatterServiceBrokerConstants.ChatterInstallNotificationsPrefix,
                                     string uninstallNotificationsStoredProcPrefix = ChatterServiceBrokerConstants.ChatterUninstallNotificationsPrefix)
            : base(connectionString)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException($"'{nameof(databaseName)}' cannot be null or whitespace", nameof(databaseName));
            }

            _databaseName = databaseName;
            _installNotificationsStoredProcPrefix = installNotificationsStoredProcPrefix;
            _uninstallNotificationsStoredProcPrefix = uninstallNotificationsStoredProcPrefix;
        }

        public override string ToString()
        {
            return string.Format(@"
                USE [{0}]

                DECLARE @db_name VARCHAR(MAX)
                SET @db_name = '{0}'               

                DECLARE @proc_name VARCHAR(MAX)
                DECLARE procedures CURSOR
                FOR
                SELECT   sys.schemas.name + '.' + sys.objects.name
                FROM    sys.objects 
                INNER JOIN sys.schemas ON sys.objects.schema_id = sys.schemas.schema_id
                WHERE sys.objects.[type] = 'P' AND sys.objects.[name] like '{2}%'

                OPEN procedures;
                FETCH NEXT FROM procedures INTO @proc_name

                WHILE (@@FETCH_STATUS = 0)
                BEGIN
                EXEC ('USE [' + @db_name + '] EXEC ' + @proc_name + ' IF (OBJECT_ID (''' 
                                + @proc_name + ''', ''P'') IS NOT NULL) DROP PROCEDURE '
                                + @proc_name)

                FETCH NEXT FROM procedures INTO @proc_name
                END

                CLOSE procedures;
                DEALLOCATE procedures;

                DECLARE procedures CURSOR
                FOR
                SELECT   sys.schemas.name + '.' + sys.objects.name
                FROM    sys.objects 
                INNER JOIN sys.schemas ON sys.objects.schema_id = sys.schemas.schema_id
                WHERE sys.objects.[type] = 'P' AND sys.objects.[name] like '{1}%'

                OPEN procedures;
                FETCH NEXT FROM procedures INTO @proc_name

                WHILE (@@FETCH_STATUS = 0)
                BEGIN
                EXEC ('USE [' + @db_name + '] DROP PROCEDURE '
                                + @proc_name)

                FETCH NEXT FROM procedures INTO @proc_name
                END

                CLOSE procedures;
                DEALLOCATE procedures;
            ", _databaseName, _installNotificationsStoredProcPrefix, _uninstallNotificationsStoredProcPrefix);
        }
    }
}
