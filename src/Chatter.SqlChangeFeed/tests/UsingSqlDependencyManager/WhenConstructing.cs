using Chatter.SqlChangeFeed.Configuration;
using FluentAssertions;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.UsingSqlDependencyManager
{
    /// <summary>
    /// Characterization tests pinning <see cref="SqlDependencyManager{TRowChangedData}"/> construction as-is.
    /// <para>
    /// <see cref="SqlDependencyManager{TRowChangedData}.InstallSqlDependencies"/> and
    /// <see cref="SqlDependencyManager{TRowChangedData}.UninstallSqlDependencies"/> are intentionally NOT
    /// exercised here: they build <c>ExecutableSqlScript</c> subclasses and call <c>.Execute()</c> against a
    /// live <c>SqlConnection</c>, which is out of scope for in-memory characterization (DEFERRED coverage gap).
    /// </para>
    /// </summary>
    public class WhenConstructing : Testing.Core.Context
    {
        [Fact]
        public void MustStoreSuppliedOptionsOnOptionsProperty()
        {
            var options = new SqlChangeFeedOptions("connection-string", "database", "table");

            var manager = new SqlDependencyManager<FakeRowData>(options);

            manager.Options.Should().BeSameAs(options);
        }

        [Fact]
        public void MustNotThrowWhenConstructedWithNullOptions()
            => FluentActions.Invoking(() => new SqlDependencyManager<FakeRowData>(null))
                .Should().NotThrow();

        [Fact]
        public void MustLeaveOptionsNullWhenConstructedWithNullOptions()
            => new SqlDependencyManager<FakeRowData>(null).Options.Should().BeNull();
    }
}
