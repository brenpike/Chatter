using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Moq;
using System;
using System.Reflection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingAssemblySourceFilterBuilder
{
    public class WhenSettingAssemblySourceProvider
    {
        private readonly Mock<IAssemblyFilterSourceProvider> _mockAssemblySourceProvider;
        private readonly Mock<Assembly> _mockAssembly;

        public WhenSettingAssemblySourceProvider()
        {
            _mockAssemblySourceProvider = new Mock<IAssemblyFilterSourceProvider>();
            _mockAssembly = new Mock<Assembly>();
            _mockAssemblySourceProvider.Setup(g => g.GetSourceAssemblies()).Returns(new Assembly[] { _mockAssembly.Object });
        }

        [Fact]
        public void MustReturnBuilder()
        {
            var retVal = AssemblySourceFilterBuilder.WithAssemblySourceProvider(_mockAssemblySourceProvider.Object);
            Assert.IsType<AssemblySourceFilterBuilder>(retVal);
        }

        [Fact]
        public void MustSetAssemblySourceProvider()
        {
            var filter = AssemblySourceFilterBuilder.WithAssemblySourceProvider(_mockAssemblySourceProvider.Object).Build();
            Assert.Equal(_mockAssemblySourceProvider.Object, filter.AssemblySourceProvider);
        }

        [Fact]
        public void MustThrowIfNullAssemblySourceProvider()
            => FluentActions.Invoking(() => AssemblySourceFilterBuilder.WithAssemblySourceProvider(null)).Should().ThrowExactly<ArgumentNullException>();
    }
}
