using Chatter.CQRS.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingNamespaceSelectorBuilder
{
    public class WhenAppendingWildcard
    {
        private readonly NamespaceSelectorBuilder _sut;

        public WhenAppendingWildcard() => _sut = NamespaceSelectorBuilder.New();

        [Fact]
        public void MustReturnBuilder()
        {
            var retVal = _sut.Append("");
            Assert.Equal(_sut, retVal);
        }

        [Fact]
        public void MustAppendWildcardSymbol()
        {
            _sut.AppendWildcard();
            Assert.Equal("*", _sut.Build());
        }
    }
}
