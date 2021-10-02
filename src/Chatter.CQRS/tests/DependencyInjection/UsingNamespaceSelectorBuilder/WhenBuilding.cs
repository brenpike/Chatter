using Chatter.CQRS.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingNamespaceSelectorBuilder
{
    public class WhenBuilding
    {
        private readonly NamespaceSelectorBuilder _sut;

        public WhenBuilding() => _sut = NamespaceSelectorBuilder.New();

        [Fact]
        public void MustReturnNamespaceSelector()
        {
            var expected = @"*str?str2\*";
            var actual = _sut.AppendWildcard()
                            .Append("str")
                            .AppendSymbolWildcard()
                            .Append("str2")
                            .AppendEscape()
                            .AppendWildcard()
                            .Build();

            Assert.Equal(expected, actual);
        }
    }
}
