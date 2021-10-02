using Chatter.CQRS.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingNamespaceSelectorBuilder
{
    public class WhenAppending
    {
        private readonly NamespaceSelectorBuilder _sut;

        public WhenAppending() => _sut = NamespaceSelectorBuilder.New();

        [Fact]
        public void MustReturnBuilder()
        {
            var retVal = _sut.Append("");
            Assert.Equal(_sut, retVal);
        }

        [Fact]
        public void MustAppendProvidedString()
        {
            var stringToAppend = "str";
            _sut.Append(stringToAppend);
            Assert.Equal(stringToAppend, _sut.Build());
        }
    }
}
