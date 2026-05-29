using FluentAssertions;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.UsingChangeTypes
{
    public class WhenCombiningFlags : Testing.Core.Context
    {
        [Fact]
        public void MustDefineNoneAsZero()
            => ((int)ChangeTypes.None).Should().Be(0);

        [Fact]
        public void MustDefineInsertAsTwo()
            // INVARIANT: Insert is 1<<1 == 2, NOT 1. Pinned explicitly.
            => ((int)ChangeTypes.Insert).Should().Be(2);

        [Fact]
        public void MustDefineUpdateAsFour()
            => ((int)ChangeTypes.Update).Should().Be(4);

        [Fact]
        public void MustDefineDeleteAsEight()
            => ((int)ChangeTypes.Delete).Should().Be(8);

        [Fact]
        public void MustHaveFlagInsertWhenCombined()
            => (ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete)
                .HasFlag(ChangeTypes.Insert).Should().BeTrue();

        [Fact]
        public void MustHaveFlagUpdateWhenCombined()
            => (ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete)
                .HasFlag(ChangeTypes.Update).Should().BeTrue();

        [Fact]
        public void MustHaveFlagDeleteWhenCombined()
            => (ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete)
                .HasFlag(ChangeTypes.Delete).Should().BeTrue();

        [Fact]
        public void MustCombineToFourteen()
            => ((int)(ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete)).Should().Be(14);

        [Fact]
        public void MustReportHasFlagNoneAsTrue()
            // INVARIANT: HasFlag(None) is always true because None == 0 (framework behavior).
            => ChangeTypes.Insert.HasFlag(ChangeTypes.None).Should().BeTrue();
    }
}
