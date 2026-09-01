using Chatter.SqlChangeFeed.Scripts.Sql;
using Chatter.SqlChangeFeed.Scripts.StoredProcedures;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.StoredProcedures.UsingSafeExecuteStoredProcedure
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Db;";
        private const string Database = "Db";
        private const string StoredProcedure = "MyProc";
        private const string Schema = "dbo";

        private static SafeExecuteStoredProcedure Create()
            => new SafeExecuteStoredProcedure(ConnectionString, Database, StoredProcedure, Schema);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConnectionStringIsNullOrWhitespace(string connectionString)
            => FluentActions.Invoking(() => new SafeExecuteStoredProcedure(connectionString, Database, StoredProcedure, Schema))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenDatabaseNameIsNullOrWhitespace(string databaseName)
            => FluentActions.Invoking(() => new SafeExecuteStoredProcedure(ConnectionString, databaseName, StoredProcedure, Schema))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenStoredProcedureNameIsNullOrWhitespace(string storedProcedureName)
            => FluentActions.Invoking(() => new SafeExecuteStoredProcedure(ConnectionString, Database, storedProcedureName, Schema))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSchemaNameIsNullOrWhitespace(string schemaName)
            => FluentActions.Invoking(() => new SafeExecuteStoredProcedure(ConnectionString, Database, StoredProcedure, schemaName))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustEmitUseDatabase()
            => Create().ToString().Should().Contain($"USE [{Database}]");

        [Fact]
        public void MustEmitObjectIdGuardedExecOfStoredProcedure()
        {
            var script = Create().ToString();
            script.Should().Contain($"IF OBJECT_ID ('[{Schema}].[{StoredProcedure}]', 'P') IS NOT NULL");
            script.Should().Contain($"EXEC [{Schema}].[{StoredProcedure}]");
        }

        [Fact]
        public void MustEscapeHostileStoredProcedureNameInObjectIdLiteralAndExec()
        {
            const string hostileStoredProcedure = "Proc]'; --";
            var script = new SafeExecuteStoredProcedure(ConnectionString, Database, hostileStoredProcedure, Schema).ToString();

            string qualifiedName = SqlIdentifier.EscapeQualified(Schema, hostileStoredProcedure);
            string quotedQualifiedName = SqlIdentifier.QuoteLiteral(qualifiedName);

            script.Should().Contain($"IF OBJECT_ID ('{quotedQualifiedName}', 'P') IS NOT NULL");
            script.Should().Contain($"EXEC {qualifiedName}");
            script.Should().NotContain("--Proc");
        }

        [Fact]
        public void MustEscapeHostileSchemaNameInObjectIdLiteralAndExec()
        {
            const string hostileSchema = "Sch]'; --ema";
            var script = new SafeExecuteStoredProcedure(ConnectionString, Database, StoredProcedure, hostileSchema).ToString();

            string qualifiedName = SqlIdentifier.EscapeQualified(hostileSchema, StoredProcedure);
            string quotedQualifiedName = SqlIdentifier.QuoteLiteral(qualifiedName);

            script.Should().Contain($"IF OBJECT_ID ('{quotedQualifiedName}', 'P') IS NOT NULL");
            script.Should().Contain($"EXEC {qualifiedName}");
        }
    }
}
