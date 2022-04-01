using Chatter.SqlChangeFeed.Scripts.ServiceBroker;
using Chatter.SqlChangeFeed.Scripts.Triggers;
using System;

namespace Chatter.SqlChangeFeed.Scripts.StoredProcedures
{
    /// <summary>
    /// Creates the stored procedure that will create necessary database objects needed for the change feed
    /// </summary>
    public class CreateInstallationProcedure : ExecutableSqlScript
    {
        private readonly string _databaseName;
        private readonly string _setupProcedureName;
        private readonly InstallAndConfigureSqlServiceBroker _serviceBrokerConfigScript;
        private readonly CreateChangeFeedTrigger _changeFeedTriggerConfigScript;
        private readonly string _tableName;
        private readonly string _schemaName;
        private readonly string _triggerName;

        /// <summary>
        /// Creates the stored procedure that will create necessary database objects needed for the change feed
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        /// <param name="databaseName">The database where the install stored proc will be created</param>
        /// <param name="setupProcedureName">The name of the stored procedure to create</param>
        /// <param name="serviceBrokerConfigScript">The script which defines all SQL Service Broker related objects</param>
        /// <param name="changeFeedTriggerConfigScript">The script which will create the trigger responsible for writing to the QUEUE when the target <paramref name="tableName"/> changes</param>
        /// <param name="tableName">The target table which will be monitored for changes</param>
        /// <param name="schemaName">The schema to use for the various objects to be created</param>
        public CreateInstallationProcedure(string connectionString,
                                           string databaseName,
                                           string setupProcedureName,
                                           InstallAndConfigureSqlServiceBroker serviceBrokerConfigScript,
                                           CreateChangeFeedTrigger changeFeedTriggerConfigScript,
                                           string tableName,
                                           string schemaName,
                                           string triggerName)
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

            _databaseName = databaseName;
            _setupProcedureName = setupProcedureName;
            _serviceBrokerConfigScript = serviceBrokerConfigScript ?? throw new ArgumentNullException(nameof(serviceBrokerConfigScript));
            _changeFeedTriggerConfigScript = changeFeedTriggerConfigScript ?? throw new ArgumentNullException(nameof(changeFeedTriggerConfigScript));
            _tableName = tableName;
            _schemaName = schemaName;
            _triggerName = triggerName;
        }

        public override string ToString()
        {
            return string.Format(@"
                USE [{0}]
                IF OBJECT_ID ('{5}.{1}', 'P') IS NULL
                BEGIN
                    EXEC ('
                        CREATE PROCEDURE {5}.{1}
                            @ExplicitCols bit = 1
                        AS
                        BEGIN
                            -- Service Broker configuration statement.
                            {2}

                            IF OBJECT_ID (''{5}.{6}'', ''TR'') IS NOT NULL
                                RETURN;

                            -- Build column collection for target table:
                            DECLARE @tbl_Columns TABLE (COLUMN_NAME sysname NOT NULL, INCLUDE_OUTPUT bit NOT NULL, PK_ORDINAL int NULL);
                            INSERT INTO @tbl_Columns (COLUMN_NAME, INCLUDE_OUTPUT, PK_ORDINAL)
                            SELECT cols.COLUMN_NAME,
	                            CASE WHEN cols.DATA_TYPE IN (''text'',''ntext'',''image'',''geometry'',''geography'') THEN 0 ELSE 1 END [INCLUDE_OUTPUT],
	                            colkeys.ORDINAL_POSITION [PK_ORDINAL]
                             FROM INFORMATION_SCHEMA.TABLES tab
                             INNER JOIN INFORMATION_SCHEMA.COLUMNS cols ON cols.TABLE_CATALOG = tab.TABLE_CATALOG
	                            AND cols.TABLE_SCHEMA = tab.TABLE_SCHEMA
	                            AND cols.TABLE_NAME = tab.TABLE_NAME
                             LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tabcon ON tabcon.TABLE_CATALOG = tab.TABLE_CATALOG
	                            AND tabcon.TABLE_SCHEMA = tab.TABLE_SCHEMA
	                            AND tabcon.TABLE_NAME = tab.TABLE_NAME
	                            AND tabcon.CONSTRAINT_TYPE = ''PRIMARY KEY''
                             LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE colkeys ON colkeys.TABLE_CATALOG = cols.TABLE_CATALOG
	                            AND colkeys.TABLE_SCHEMA = cols.TABLE_SCHEMA
	                            AND colkeys.TABLE_NAME = cols.TABLE_NAME
	                            AND colkeys.COLUMN_NAME = cols.COLUMN_NAME
	                            AND colkeys.CONSTRAINT_NAME = tabcon.CONSTRAINT_NAME
                             WHERE tab.TABLE_CATALOG = ''{0}''
	                            AND tab.TABLE_SCHEMA = ''{5}''
	                            AND tab.TABLE_NAME = ''{4}'';

                            -- Construct column and join column strings:
                            DECLARE @ColumnList nvarchar(max) = '''';
                            SELECT @ColumnList = @ColumnList + '',%PFX%.['' + COLUMN_NAME + '']'' FROM @tbl_Columns;
                            DECLARE @JoinColumns nvarchar(max) = '''';
                            SELECT @JoinColumns = @JoinColumns + '' AND del.['' + COLUMN_NAME + ''] = ins.['' + COLUMN_NAME + '']''
                             FROM @tbl_Columns
                             WHERE PK_ORDINAL IS NOT NULL
                             ORDER BY PK_ORDINAL;

                            -- Construct statement for trigger to actually build message content:
                            DECLARE @TriggerMessageStatement nvarchar(max) = ''
                            SET @Message = (
                            SELECT
	                            JSON_QUERY(NULLIF(JSON_QUERY((SELECT '' + CASE @ExplicitCols WHEN 1 THEN REPLACE(SUBSTRING(@ColumnList, 2, LEN(@ColumnList)), ''%PFX%.'', ''ins.'') ELSE ''ins.*'' END + '' FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)), ''''{{}}'''')) [Inserted],
	                            JSON_QUERY(NULLIF(JSON_QUERY((SELECT '' + CASE @ExplicitCols WHEN 1 THEN REPLACE(SUBSTRING(@ColumnList, 2, LEN(@ColumnList)), ''%PFX%.'', ''del.'') ELSE ''del.*'' END + '' FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)), ''''{{}}'''')) [Deleted]
                            FROM INSERTED ins
                            FULL OUTER JOIN DELETED del ON '' + SUBSTRING(@JoinColumns, 6, LEN(@JoinColumns)) + ''
                            FOR JSON AUTO
                            );
                            SET @message = (SELECT JSON_QUERY(@message) [Changes] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);'';

                            -- Change Feed Trigger configuration statement.
                            DECLARE @triggerStatement NVARCHAR(MAX) = REPLACE(CONVERT(nvarchar(max), N''{3}''), ''%set_message_statement%'', @TriggerMessageStatement);

                            EXEC sp_executesql @triggerStatement
                        END
                        ')
                END
            ", _databaseName, _setupProcedureName, _serviceBrokerConfigScript.ToString().Replace("'", "''"), _changeFeedTriggerConfigScript.ToString().Replace("'", "''''"), _tableName, _schemaName, _triggerName);
        }
    }
}
