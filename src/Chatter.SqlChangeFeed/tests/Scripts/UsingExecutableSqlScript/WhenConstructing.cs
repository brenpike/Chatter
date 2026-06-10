using Chatter.SqlChangeFeed.Scripts;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.UsingExecutableSqlScript
{
    /// <summary>
    /// Pins the connection-string guard in the <see cref="ExecutableSqlScript"/> base constructor as-is.
    /// <para>
    /// <see cref="ExecutableSqlScript.Execute"/> and <see cref="ExecutableSqlScript.ExecuteAsync"/> are
    /// intentionally NOT exercised here: both open a live <c>SqlConnection</c> against the stored
    /// connection string, which is out of scope for in-memory characterization (DEFERRED coverage gap),
    /// mirroring the <c>UsingSqlDependencyManager.WhenConstructing</c> precedent.
    /// </para>
    /// </summary>
    public class WhenConstructing : Testing.Core.Context
    {
        private sealed class TestScript : ExecutableSqlScript
        {
            public TestScript(string connectionString) : base(connectionString)
            {
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowArgumentExceptionWhenConnectionStringIsNullOrWhitespace(string connectionString)
            => FluentActions.Invoking(() => new TestScript(connectionString))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustNotThrowWhenConnectionStringIsValid()
            => FluentActions.Invoking(() => new TestScript("Server=.;Database=Db;"))
                .Should().NotThrow();

        [Fact]
        public void MustProduceUsableSubclassWhenConnectionStringIsValid()
            => new TestScript("Server=.;Database=Db;").Should().BeAssignableTo<ExecutableSqlScript>();
    }
}
