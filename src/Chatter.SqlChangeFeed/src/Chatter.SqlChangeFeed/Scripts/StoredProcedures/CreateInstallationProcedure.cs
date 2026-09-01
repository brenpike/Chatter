using Chatter.SqlChangeFeed.Scripts.ServiceBroker;
using Chatter.SqlChangeFeed.Scripts.Sql;
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

            if (string.IsNullOrWhiteSpace(triggerName))
            {
                throw new ArgumentException($"'{nameof(triggerName)}' cannot be null or whitespace", nameof(triggerName));
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
            // INVARIANT: everything spliced inside EXEC(' ... ') sits one single-quoted layer deep, so the created
            // procedure name is quoted at depth 1 and every literal nested inside the procedure body at depth 2.
            var watchedTableAsNestedLiteral = SqlIdentifier.QuoteLiteral(SqlIdentifier.EscapeQualified(_schemaName, _tableName), 2);

            // INVARIANT: CREATE OR ALTER replaces an existence guard so an already-installed database receives the
            // current procedure body instead of silently keeping a stale one. Requires SQL Server 2016 SP1.
            // INVARIANT: every precondition is checked before the Service Broker section, so a refusal cannot leave
            // a partially created queue, service, or trigger behind.
            // INVARIANT: the trigger's column list is re-derived from INFORMATION_SCHEMA on every run and compared
            // against the fingerprint the installed trigger carries, so a watched table whose columns drifted gets a
            // refreshed trigger instead of one referencing a column that no longer exists.
            return string.Format(@"
                USE {0}
                EXEC ('
                    CREATE OR ALTER PROCEDURE {1}
                        @ExplicitCols bit = 1
                    AS
                    BEGIN
                        -- Precondition: Azure SQL Database (EngineEdition 5) has no Service Broker at all. Azure SQL
                        -- Managed Instance (EngineEdition 8) does, and remains supported.
                        IF CONVERT(int, SERVERPROPERTY(''EngineEdition'')) = 5
                        BEGIN
                            RAISERROR(''Chatter change feed cannot be installed on Azure SQL Database: SQL Service Broker is not available on this engine edition. Watched table: {8}.'', 16, 1);
                            RETURN;
                        END

                        -- Precondition: the watched table must exist before a trigger can be created on it.
                        IF OBJECT_ID (''{8}'', ''U'') IS NULL
                        BEGIN
                            RAISERROR(''Chatter change feed cannot be installed: the watched table {8} does not exist.'', 16, 1);
                            RETURN;
                        END

                        -- Build column collection for target table:
                        DECLARE @tbl_Columns TABLE (COLUMN_NAME sysname NOT NULL, INCLUDE_OUTPUT bit NOT NULL, PK_ORDINAL int NULL, COLUMN_ORDINAL int NOT NULL);
                        INSERT INTO @tbl_Columns (COLUMN_NAME, INCLUDE_OUTPUT, PK_ORDINAL, COLUMN_ORDINAL)
                        SELECT cols.COLUMN_NAME,
	                        CASE WHEN cols.DATA_TYPE IN (''text'',''ntext'',''image'',''geometry'',''geography'') THEN 0 ELSE 1 END [INCLUDE_OUTPUT],
	                        colkeys.ORDINAL_POSITION [PK_ORDINAL],
	                        cols.ORDINAL_POSITION [COLUMN_ORDINAL]
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
                         WHERE tab.TABLE_CATALOG = ''{7}''
	                        AND tab.TABLE_SCHEMA = ''{5}''
	                        AND tab.TABLE_NAME = ''{4}'';

                        -- Precondition: PK_ORDINAL is populated only from a PRIMARY KEY constraint, so a table
                        -- carrying only a UNIQUE constraint is correctly refused here.
                        IF NOT EXISTS (SELECT 1 FROM @tbl_Columns WHERE PK_ORDINAL IS NOT NULL)
                        BEGIN
                            RAISERROR(''Chatter change feed cannot be installed: the watched table {8} has no PRIMARY KEY. The change feed trigger joins INSERTED to DELETED on the primary key columns.'', 16, 1);
                            RETURN;
                        END

                        -- Service Broker configuration statement.
                        {2}

                        -- Fingerprint the CURRENT column set of the watched table. It is derived from the same
                        -- @tbl_Columns collection the trigger''s SELECT and join lists are built from, so it changes
                        -- exactly when the emitted trigger body would change: column names, their ordinals (the
                        -- emitted SELECT lists the columns in @tbl_Columns order, which is ordinal order) and their
                        -- PRIMARY KEY ordinals (the emitted join is explicitly ordered by them). It is HASHED rather
                        -- than embedded verbatim because a column name may contain a line break, which would break
                        -- the single-line marker comment below.
                        -- INVARIANT: the serialization is INJECTIVE - each column name is LENGTH-PREFIXED with its
                        -- character count. A delimited identifier may legally contain the '':'' and ''|'' separators, so
                        -- concatenating unescaped fields does not uniquely represent a column set: names
                        -- ''a:|3:b'',''c'' and ''a'',''b:|3:c'' serialize identically without the prefix, the hash does not
                        -- change across that rename, and the migration returns leaving the trigger bound to the old
                        -- column list. The length is taken with DATALENGTH/2 rather than LEN because LEN ignores
                        -- trailing spaces, which are significant in a delimited identifier.
                        DECLARE @ColumnFingerprintMarker nvarchar(50) = ''-- chatter-change-feed-columns: '';
                        DECLARE @ColumnSignature nvarchar(max) =
                            (SELECT CONVERT(nvarchar(20), COLUMN_ORDINAL) + '':'' + CONVERT(nvarchar(20), DATALENGTH(COLUMN_NAME) / 2) + '':'' + COLUMN_NAME + '':'' + ISNULL(CONVERT(nvarchar(20), PK_ORDINAL), '''') + ''|''
                             FROM @tbl_Columns
                             ORDER BY COLUMN_ORDINAL
                             FOR XML PATH(''''), TYPE).value(''.'', ''nvarchar(max)'');
                        DECLARE @ColumnFingerprint nvarchar(64) = CONVERT(nvarchar(64), HASHBYTES(''SHA2_256'', @ColumnSignature), 2);

                        -- INVARIANT: only the trigger installed ON THE WATCHED TABLE is a refresh candidate. A
                        -- same-named trigger on another table is left alone, so the CREATE below fails loudly on the
                        -- duplicate name rather than dropping an object the change feed does not own.
                        DECLARE @InstalledTriggerId int = (SELECT trg.object_id
                                                             FROM sys.triggers trg
                                                            WHERE trg.object_id = OBJECT_ID (''{6}'', ''TR'')
                                                              AND trg.parent_id = OBJECT_ID (''{8}'', ''U''));

                        IF @InstalledTriggerId IS NOT NULL
                        BEGIN
                            -- The installed trigger already carries the current fingerprint: leave it untouched.
                            IF EXISTS (SELECT 1
                                         FROM sys.sql_modules
                                        WHERE object_id = @InstalledTriggerId
                                          AND CHARINDEX(@ColumnFingerprintMarker + @ColumnFingerprint, definition) > 0)
                                RETURN;

                            -- Drift, or a marker-less trigger installed by an earlier package version: drop it so the
                            -- CREATE below re-derives the column list from INFORMATION_SCHEMA rather than leaving a
                            -- snapshot taken at some past install in place.
                            DROP TRIGGER {9};
                        END

                        -- Construct column and join column strings:
                        -- INVARIANT: live column names are delimited by QUOTENAME, never by hand-written brackets.
                        -- COLUMN_NAME is read back from the watched table''s own catalog rows, so it can legally
                        -- carry a closing bracket; concatenating bare bracket characters around it lets that
                        -- bracket close the identifier early and break out into the trigger body built below.
                        -- QUOTENAME is the server-side counterpart of the SqlIdentifier primitive the C# emitters
                        -- use for the names known at emit time; these names are only known at install time.
                        DECLARE @ColumnList nvarchar(max) = '''';
                        SELECT @ColumnList = @ColumnList + '',%PFX%.'' + QUOTENAME(COLUMN_NAME) FROM @tbl_Columns;
                        DECLARE @JoinColumns nvarchar(max) = '''';
                        SELECT @JoinColumns = @JoinColumns + '' AND del.'' + QUOTENAME(COLUMN_NAME) + '' = ins.'' + QUOTENAME(COLUMN_NAME)
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

                        -- Change Feed Trigger configuration statement. The fingerprint rides in a leading comment so
                        -- the next migration run reads it back from sys.sql_modules.definition and refreshes the
                        -- trigger only when the watched table''s column set has actually drifted.
                        DECLARE @triggerStatement NVARCHAR(MAX) = @ColumnFingerprintMarker + @ColumnFingerprint + CHAR(13) + CHAR(10) +
                            REPLACE(CONVERT(nvarchar(max), N''{3}''), ''%set_message_statement%'', @TriggerMessageStatement);

                        EXEC sp_executesql @triggerStatement
                    END
                    ')
            ", SqlIdentifier.Escape(_databaseName),
               SqlIdentifier.QuoteLiteral(SqlIdentifier.EscapeQualified(_schemaName, _setupProcedureName)),
               SqlIdentifier.QuoteLiteral(_serviceBrokerConfigScript.ToString()),
               SqlIdentifier.QuoteLiteral(_changeFeedTriggerConfigScript.ToString(), 2),
               SqlIdentifier.QuoteLiteral(_tableName, 2),
               SqlIdentifier.QuoteLiteral(_schemaName, 2),
               SqlIdentifier.QuoteLiteral(SqlIdentifier.EscapeQualified(_schemaName, _triggerName), 2),
               SqlIdentifier.QuoteLiteral(_databaseName, 2),
               watchedTableAsNestedLiteral,
               SqlIdentifier.QuoteLiteral(SqlIdentifier.EscapeQualified(_schemaName, _triggerName)));
        }
    }
}
