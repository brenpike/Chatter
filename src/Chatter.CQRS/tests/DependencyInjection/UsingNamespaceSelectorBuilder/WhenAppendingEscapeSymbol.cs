using Chatter.CQRS.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingNamespaceSelectorBuilder
{
    public class WhenAppendingEscapeSymbol
    {
        private readonly NamespaceSelectorBuilder _sut;

        public WhenAppendingEscapeSymbol() => _sut = NamespaceSelectorBuilder.New();

        [Fact]
        public void MustReturnBuilder()
        {
            var retVal = _sut.Append("");
            Assert.Equal(_sut, retVal);
        }

        [Fact]
        public void MustAppendEscapeSymbol()
        {
            _sut.AppendEscape();
            Assert.Equal(@"\", _sut.Build());
        }
    }
}
