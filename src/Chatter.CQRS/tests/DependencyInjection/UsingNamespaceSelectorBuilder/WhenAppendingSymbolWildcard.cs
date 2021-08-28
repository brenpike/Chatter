using Chatter.CQRS.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingNamespaceSelectorBuilder
{
    public class WhenAppendingSymbolWildcard
    {
        private readonly NamespaceSelectorBuilder _sut;

        public WhenAppendingSymbolWildcard() => _sut = NamespaceSelectorBuilder.New();

        [Fact]
        public void MustReturnBuilder()
        {
            var retVal = _sut.Append("");
            Assert.Equal(_sut, retVal);
        }

        [Fact]
        public void MustAppendSymbolWildcard()
        {
            _sut.AppendSymbolWildcard();
            Assert.Equal("?", _sut.Build());
        }
    }
}
