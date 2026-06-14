using Chatter.SqlChangeFeed.Scripts;
using FluentAssertions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.UsingExecutableSqlScript
{
    /// <summary>
    /// Pins that <see cref="ExecutableSqlScript.ExecuteAsync"/> observes a pre-cancelled token and
    /// short-circuits before any network I/O. A pre-cancelled token makes <c>SqlConnection.OpenAsync</c>
    /// return a cancelled task immediately, so no live database is required (no Integration trait).
    /// </summary>
    public class WhenCancelling : Testing.Core.Context
    {
        private sealed class TestScript : ExecutableSqlScript
        {
            public TestScript(string connectionString) : base(connectionString)
            {
            }

            public override string ToString() => "SELECT 1";
        }

        [Fact]
        public Task MustThrowOperationCanceledExceptionWhenTokenIsAlreadyCancelled()
        {
            var script = new TestScript("Server=.;Database=Db;");

            return FluentActions.Awaiting(() => script.ExecuteAsync(new CancellationToken(canceled: true)))
                .Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
