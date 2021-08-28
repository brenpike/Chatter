using Chatter.CQRS.DependencyInjection;
using Moq;
using System.Reflection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingAssemblySourceFilterBuilder
{
    public class WhenBuilding
    {
        private readonly AssemblySourceFilterBuilder _sut;
        private readonly Mock<IAssemblySourceProvider> _mockAssemblySourceProvider;
        private readonly Mock<Assembly> _mockAssembly;

        public WhenBuilding()
        {
            _sut = AssemblySourceFilterBuilder.New();
            _mockAssemblySourceProvider = new Mock<IAssemblySourceProvider>();
            _mockAssembly = new Mock<Assembly>();
            _mockAssemblySourceProvider.Setup(g => g.GetSourceAssemblies()).Returns(new Assembly[] { _mockAssembly.Object });
        }

        [Fact]
        public void MustReturnFilter()
        {
            var namespaceSelector = "test";
            _sut.WithNamespaceSelector(namespaceSelector);

            var retVal = _sut.Build();
            Assert.IsType<AssemblySourceFilter>(retVal);
            Assert.Equal(namespaceSelector, retVal.NamespaceSelector);
        }

        [Fact]
        public void MustDefaultAssemblySourceProviderToCurrentAppDomainAssemblyProviderIfNotProvided()
        {
            var filter = _sut.Build();
            Assert.IsType<CurrentAppDomainAssemblyProvider>(filter.AssemblySourceProvider);
        }
    }
}
