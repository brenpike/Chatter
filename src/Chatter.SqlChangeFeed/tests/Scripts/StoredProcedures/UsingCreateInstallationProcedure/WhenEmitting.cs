using Chatter.SqlChangeFeed.Scripts.ServiceBroker;
using Chatter.SqlChangeFeed.Scripts.StoredProcedures;
using Chatter.SqlChangeFeed.Scripts.Triggers;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.StoredProcedures.UsingCreateInstallationProcedure
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Db;";
        private const string Database = "Db";
        private const string SetupProcedure = "MyInstallProc";
        private const string Table = "MyTable";
        private const string Schema = "dbo";
        private const string Trigger = "MyTrigger";
        private const string Service = "MyService";
        private const string HostileDatabase = "My]Db'; DROP TABLE Users;--";
        private const string HostileSetupProcedure = "My]Proc'; DROP TABLE Users;--";
        private const string HostileTable = "My]Table'; DROP TABLE Users;--";
        private const string HostileSchema = "My]Schema'; DROP TABLE Users;--";
        private const string HostileTrigger = "My]Trigger'; DROP TABLE Users;--";

        // The three precondition gates, spelled exactly as the emitted procedure body carries them (nested one
        // single-quoted layer deep inside the EXEC(' ... ') that creates the procedure).
        private const string EngineEditionGate = "IF CONVERT(int, SERVERPROPERTY(''EngineEdition'')) = 5";
        private const string TableExistsGate = "IF OBJECT_ID (''[" + Schema + "].[" + Table + "]'', ''U'') IS NULL";
        private const string PrimaryKeyGate = "IF NOT EXISTS (SELECT 1 FROM @tbl_Columns WHERE PK_ORDINAL IS NOT NULL)";

        // The column-set fingerprint the emitted procedure derives, embeds in the trigger and compares against on
        // the next run, spelled exactly as the emitted procedure body carries them.
        private const string FingerprintMarkerDeclaration = "DECLARE @ColumnFingerprintMarker nvarchar(50) = ''-- chatter-change-feed-columns: '';";
        private const string FingerprintHash = "CONVERT(nvarchar(64), HASHBYTES(''SHA2_256'', @ColumnSignature), 2)";
        // The per-column term of the signature the fingerprint hashes. The column name is LENGTH-PREFIXED:
        // a delimited identifier may legally contain the ':' and '|' separators, so an unprefixed
        // concatenation is not injective and two different column sets can serialize identically.
        private const string FingerprintSignatureTerm =
            "CONVERT(nvarchar(20), COLUMN_ORDINAL) + '':'' + CONVERT(nvarchar(20), DATALENGTH(COLUMN_NAME) / 2) + '':'' + COLUMN_NAME + '':'' + ISNULL(CONVERT(nvarchar(20), PK_ORDINAL), '''') + ''|''";
        private const string AmbiguousFingerprintSignatureTerm =
            "CONVERT(nvarchar(20), COLUMN_ORDINAL) + '':'' + COLUMN_NAME + '':''";
        private const string InstalledTriggerLookup = "DECLARE @InstalledTriggerId int";
        private const string FingerprintComparison = "CHARINDEX(@ColumnFingerprintMarker + @ColumnFingerprint, definition) > 0";
        private const string FingerprintMarkerEmbed = "@ColumnFingerprintMarker + @ColumnFingerprint + CHAR(13) + CHAR(10)";

        private static InstallAndConfigureSqlServiceBroker ServiceBrokerScript()
            => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, "MyQueue", Service, Schema, "MyDeadLetterQueue", "MyDeadLetterService");

        private static CreateChangeFeedTrigger TriggerScript()
            => new CreateChangeFeedTrigger(Table, Trigger, ChangeTypes.Insert, Service, Schema);

        private static CreateInstallationProcedure Create()
            => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, Schema, Trigger);

        private static CreateInstallationProcedure CreateHostile()
            => new CreateInstallationProcedure(ConnectionString, HostileDatabase, HostileSetupProcedure, ServiceBrokerScript(), TriggerScript(), HostileTable, HostileSchema, HostileTrigger);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConnectionStringIsNullOrWhitespace(string connectionString)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(connectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, Schema, Trigger))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenDatabaseNameIsNullOrWhitespace(string databaseName)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, databaseName, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, Schema, Trigger))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSetupProcedureNameIsNullOrWhitespace(string setupProcedureName)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, setupProcedureName, ServiceBrokerScript(), TriggerScript(), Table, Schema, Trigger))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenTableNameIsNullOrWhitespace(string tableName)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), tableName, Schema, Trigger))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSchemaNameIsNullOrWhitespace(string schemaName)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, schemaName, Trigger))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServiceBrokerConfigScriptIsNull()
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, null, TriggerScript(), Table, Schema, Trigger))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenChangeFeedTriggerConfigScriptIsNull()
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), null, Table, Schema, Trigger))
                .Should().Throw<ArgumentNullException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenTriggerNameIsNullOrWhitespace(string triggerName)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, Schema, triggerName))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustEmitUseDatabase()
            => Create().ToString().Should().Contain($"USE [{Database}]");

        [Fact]
        public void MustEmitCreateOrAlterProcedureWithSchemaAndProcedureName()
            => Create().ToString().Should().Contain($"CREATE OR ALTER PROCEDURE [{Schema}].[{SetupProcedure}]");

        [Fact]
        public void MustNotGuardCreationOnTheInstallProcedureNotAlreadyExisting()
            => Create().ToString().Should().NotContain($"OBJECT_ID ('[{Schema}].[{SetupProcedure}]', 'P')");

        [Fact]
        public void MustEmitObjectIdProbeForSchemaAndTriggerName()
            => Create().ToString().Should().Contain($"OBJECT_ID (''[{Schema}].[{Trigger}]'', ''TR'')");

        [Fact]
        public void MustEmitExplicitColsBitParameter()
            => Create().ToString().Should().Contain("@ExplicitCols bit = 1");

        [Fact]
        public void MustQuoteNameLiveColumnNamesInTheColumnList()
            => Create().ToString().Should().Contain("@ColumnList + '',%PFX%.'' + QUOTENAME(COLUMN_NAME)");

        [Fact]
        public void MustQuoteNameLiveColumnNamesInTheJoinColumns()
            => Create().ToString().Should().Contain("@JoinColumns + '' AND del.'' + QUOTENAME(COLUMN_NAME) + '' = ins.'' + QUOTENAME(COLUMN_NAME)");

        [Fact]
        public void MustNotHandBracketLiveColumnNames()
            => Create().ToString().Should().NotContain("['' + COLUMN_NAME + '']");

        [Fact]
        public void MustEmitNestedServiceBrokerScriptWithDoubledQuotes()
        {
            // The nested service broker script's single quotes are doubled ('' ).
            Create().ToString().Should().Contain($"name = ''{Database}''");
        }

        [Fact]
        public void MustEmitNestedTriggerScriptWithQuadrupledQuotes()
        {
            // The nested trigger script's single quotes are quadrupled ('''' ).
            Create().ToString().Should().Contain($"''''{Service}''''");
        }

        [Fact]
        public void MustBracketEscapeHostileDatabaseNameInUseStatement()
            => CreateHostile().ToString().Should().Contain("USE [My]]Db'; DROP TABLE Users;--]");

        [Fact]
        public void MustNotEmitAnExistenceGuardForTheHostileInstallProcedureName()
            => CreateHostile().ToString()
                .Should().NotContain("OBJECT_ID ('[My]]Schema''; DROP TABLE Users;--].[My]]Proc''; DROP TABLE Users;--]', 'P')");

        [Fact]
        public void MustEscapeHostileSchemaAndProcedureNameInCreateOrAlterProcedure()
            => CreateHostile().ToString()
                .Should().Contain("CREATE OR ALTER PROCEDURE [My]]Schema''; DROP TABLE Users;--].[My]]Proc''; DROP TABLE Users;--]");

        [Fact]
        public void MustEscapeHostileSchemaAndTriggerNameInNestedObjectIdProbe()
            => CreateHostile().ToString()
                .Should().Contain("OBJECT_ID (''[My]]Schema''''; DROP TABLE Users;--].[My]]Trigger''''; DROP TABLE Users;--]'', ''TR'')");

        [Fact]
        public void MustQuadrupleQuoteHostileDatabaseNameInNestedCatalogLiteral()
            => CreateHostile().ToString()
                .Should().Contain("tab.TABLE_CATALOG = ''My]Db''''; DROP TABLE Users;--''");

        [Fact]
        public void MustQuadrupleQuoteHostileSchemaNameInNestedSchemaLiteral()
            => CreateHostile().ToString()
                .Should().Contain("tab.TABLE_SCHEMA = ''My]Schema''''; DROP TABLE Users;--''");

        [Fact]
        public void MustQuadrupleQuoteHostileTableNameInNestedTableLiteral()
            => CreateHostile().ToString()
                .Should().Contain("tab.TABLE_NAME = ''My]Table''''; DROP TABLE Users;--''");

        [Fact]
        public void MustGateOnAzureSqlDatabaseEngineEdition()
            => Create().ToString().Should().Contain(EngineEditionGate);

        [Fact]
        public void MustGateOnTheWatchedTableExisting()
            => Create().ToString().Should().Contain(TableExistsGate);

        [Fact]
        public void MustGateOnTheWatchedTableHavingAPrimaryKey()
            => Create().ToString().Should().Contain(PrimaryKeyGate);

        [Fact]
        public void MustNameTheAzureSqlDatabaseCauseAndTheWatchedTableInItsPreconditionError()
            => Create().ToString().Should().Contain(
                $"RAISERROR(''Chatter change feed cannot be installed on Azure SQL Database: SQL Service Broker is not available on this engine edition. Watched table: [{Schema}].[{Table}].'', 16, 1);");

        [Fact]
        public void MustNameTheMissingTableCauseAndTheWatchedTableInItsPreconditionError()
            => Create().ToString().Should().Contain(
                $"RAISERROR(''Chatter change feed cannot be installed: the watched table [{Schema}].[{Table}] does not exist.'', 16, 1);");

        [Fact]
        public void MustNameTheMissingPrimaryKeyCauseAndTheWatchedTableInItsPreconditionError()
            => Create().ToString().Should().Contain(
                $"RAISERROR(''Chatter change feed cannot be installed: the watched table [{Schema}].[{Table}] has no PRIMARY KEY. The change feed trigger joins INSERTED to DELETED on the primary key columns.'', 16, 1);");

        [Fact]
        public void MustQuadrupleQuoteHostileSchemaAndTableNameInTheTableExistenceGate()
            => CreateHostile().ToString()
                .Should().Contain("OBJECT_ID (''[My]]Schema''''; DROP TABLE Users;--].[My]]Table''''; DROP TABLE Users;--]'', ''U'')");

        [Fact]
        public void MustMaterializeTheColumnCollectionBeforeAnyServiceBrokerObjectIsCreated()
        {
            var script = Create().ToString();

            var columnCollection = IndexOfOrFail(script, "INSERT INTO @tbl_Columns");
            var enableBroker = IndexOfOrFail(script, "SET ENABLE_BROKER");

            columnCollection.Should().BeLessThan(enableBroker);
        }

        [Fact]
        public void MustRunEveryPreconditionBeforeAnyObjectIsCreated()
        {
            var script = Create().ToString();

            var engineEditionGate = IndexOfOrFail(script, EngineEditionGate);
            var tableExistsGate = IndexOfOrFail(script, TableExistsGate);
            var primaryKeyGate = IndexOfOrFail(script, PrimaryKeyGate);
            var enableBroker = IndexOfOrFail(script, "SET ENABLE_BROKER");
            var createQueue = IndexOfOrFail(script, "CREATE QUEUE");
            var createService = IndexOfOrFail(script, "CREATE SERVICE");
            var createTrigger = IndexOfOrFail(script, "CREATE TRIGGER");

            tableExistsGate.Should().BeGreaterThan(engineEditionGate);
            primaryKeyGate.Should().BeGreaterThan(tableExistsGate);
            enableBroker.Should().BeGreaterThan(primaryKeyGate);
            createQueue.Should().BeGreaterThan(primaryKeyGate);
            createService.Should().BeGreaterThan(primaryKeyGate);
            createTrigger.Should().BeGreaterThan(primaryKeyGate);
        }

        [Fact]
        public void MustCarryTheColumnOrdinalInTheMaterializedColumnCollection()
        {
            var script = Create().ToString();

            script.Should().Contain("COLUMN_ORDINAL int NOT NULL");
            script.Should().Contain("cols.ORDINAL_POSITION [COLUMN_ORDINAL]");
        }

        [Fact]
        public void MustDeriveTheColumnFingerprintFromTheMaterializedColumnCollection()
        {
            var script = Create().ToString();

            script.Should().Contain(FingerprintMarkerDeclaration);
            script.Should().Contain(FingerprintHash);
            script.Should().Contain("FROM @tbl_Columns");
        }

        [Fact]
        public void MustOrderTheColumnFingerprintByColumnOrdinal()
            => Create().ToString().Should().Contain("ORDER BY COLUMN_ORDINAL");

        [Fact]
        public void MustLengthPrefixTheColumnNameInTheFingerprintSignature()
            => Create().ToString().Should().Contain(FingerprintSignatureTerm);

        [Fact]
        public void MustNotConcatenateTheColumnNameIntoTheFingerprintSignatureWithoutALengthPrefix()
            => Create().ToString().Should().NotContain(AmbiguousFingerprintSignatureTerm);

        [Fact]
        public void MustEmbedTheColumnFingerprintMarkerInTheTriggerStatement()
            => Create().ToString().Should().Contain(FingerprintMarkerEmbed);

        [Fact]
        public void MustLookUpTheInstalledTriggerOnTheWatchedTable()
        {
            var script = Create().ToString();

            script.Should().Contain(InstalledTriggerLookup);
            script.Should().Contain($"trg.parent_id = OBJECT_ID (''[{Schema}].[{Table}]'', ''U'')");
        }

        [Fact]
        public void MustReturnOnlyWhenTheInstalledTriggerCarriesTheCurrentColumnFingerprint()
            => Create().ToString().Should().Contain(FingerprintComparison);

        [Fact]
        public void MustDropTheInstalledTriggerWhenItsColumnFingerprintDoesNotMatch()
            => Create().ToString().Should().Contain($"DROP TRIGGER [{Schema}].[{Trigger}];");

        [Fact]
        public void MustNotReturnUnconditionallyWhenTheTriggerAlreadyExists()
        {
            var script = Create().ToString();

            var columnCollection = IndexOfOrFail(script, "INSERT INTO @tbl_Columns");
            var fingerprint = IndexOfOrFail(script, FingerprintHash);
            var installedTrigger = IndexOfOrFail(script, InstalledTriggerLookup);
            var comparison = IndexOfOrFail(script, FingerprintComparison);
            var dropTrigger = IndexOfOrFail(script, $"DROP TRIGGER [{Schema}].[{Trigger}];");
            var createTrigger = IndexOfOrFail(script, "CREATE TRIGGER");

            fingerprint.Should().BeGreaterThan(columnCollection, "the fingerprint is derived from the materialized column collection");
            installedTrigger.Should().BeGreaterThan(fingerprint);
            comparison.Should().BeGreaterThan(installedTrigger, "an existing trigger is compared, never trusted");
            dropTrigger.Should().BeGreaterThan(comparison, "the trigger is dropped only when the comparison fails");
            createTrigger.Should().BeGreaterThan(dropTrigger, "the dropped trigger is recreated from the current column set");
        }

        [Fact]
        public void MustEscapeHostileSchemaAndTriggerNameInTheDropTriggerStatement()
            => CreateHostile().ToString()
                .Should().Contain("DROP TRIGGER [My]]Schema''; DROP TABLE Users;--].[My]]Trigger''; DROP TABLE Users;--];");

        private static int IndexOfOrFail(string script, string fragment)
        {
            var index = script.IndexOf(fragment, StringComparison.Ordinal);
            index.Should().BeGreaterThan(-1, $"the emitted script must contain '{fragment}'");
            return index;
        }
    }
}
