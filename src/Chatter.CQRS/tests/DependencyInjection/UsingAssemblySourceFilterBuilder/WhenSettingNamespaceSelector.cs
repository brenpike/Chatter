using Chatter.CQRS.DependencyInjection;
using Moq;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingAssemblySourceFilterBuilder
{
    public class WhenSettingNamespaceSelector
    {
        private readonly AssemblySourceFilterBuilder _sut;

        public WhenSettingNamespaceSelector() => _sut = AssemblySourceFilterBuilder.New();

        [Fact]
        public void MustReturnBuilder()
        {
            var retVal = _sut.WithNamespaceSelector("test");
            Assert.IsType<AssemblySourceFilterBuilder>(retVal);
            Assert.Same(retVal, _sut);
        }

        [Fact]
        public void MustSetNamespaceSelector()
        {
            var selector = "test";
            var filter = _sut.WithNamespaceSelector(selector).Build();
            Assert.Equal(selector, filter.NamespaceSelector);
        }

        [Fact]
        public void MustSetNamespaceSelectorUsingBuilder()
        {
            var selector = "test";
            var filter = _sut.WithNamespaceSelector(b => b.Append(selector)).Build();
            Assert.Equal(selector, filter.NamespaceSelector);
        }
    }
}
