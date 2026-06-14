using Chatter.SqlChangeFeed.Configuration;
using FluentAssertions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.UsingSqlDependencyManager
{
    /// <summary>
    /// Pins that <see cref="SqlDependencyManager{TRowChangedData}"/> propagates a pre-cancelled token through
    /// both install and uninstall, short-circuiting before any real DDL is executed. A pre-cancelled token
    /// makes the underlying <c>OpenAsync</c> return cancelled immediately, so no live database is required
    /// (no Integration trait).
    /// </summary>
    public class WhenCancelling : Testing.Core.Context
    {
        private static SqlDependencyManager<FakeRowData> CreateManager()
            => new SqlDependencyManager<FakeRowData>(
                new SqlChangeFeedOptions("Server=.;Database=Db;", "Db", "table"));

        [Fact]
        public async Task MustThrowOperationCanceledExceptionWhenInstallingWithCancelledToken()
        {
            var manager = CreateManager();

            await FluentActions.Awaiting(() => manager.InstallSqlDependencies(
                    "installProc",
                    "uninstallProc",
                    "queue",
                    "service",
                    "trigger",
                    "deadLetterQueue",
                    "deadLetterService",
                    new CancellationToken(canceled: true)))
                .Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task MustThrowOperationCanceledExceptionWhenUninstallingWithCancelledToken()
        {
            var manager = CreateManager();

            await FluentActions.Awaiting(() => manager.UninstallSqlDependencies(
                    "uninstallProc",
                    new CancellationToken(canceled: true)))
                .Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
