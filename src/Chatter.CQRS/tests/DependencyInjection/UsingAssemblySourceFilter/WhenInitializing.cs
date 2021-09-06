using Chatter.CQRS.DependencyInjection;
using Chatter.Testing.Core.Creators.CQRS;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingAssemblySourceFilter
{
    public class WhenInitializing : Testing.Core.Context
    {
        [Fact]
        public void MustSetAssemblyProviderSource()
        {
            var asf = New.Cqrs().AssemblyFilterSourceProvider.Creation;
            var sut = new AssemblySourceFilter(asf, It.IsAny<string>(), It.IsAny<IEnumerable<Assembly>>());
            Assert.Equal(asf, sut.AssemblySourceProvider);
        }

        [Fact]
        public void MustThrowIfAssemblyProviderSourceIsNull()
        {
            var asf = new Mock<IAssemblyFilterSourceProvider>();
            FluentActions.Invoking(() => new AssemblySourceFilter(null, It.IsAny<string>(), It.IsAny<IEnumerable<Assembly>>())).Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void MustSetNamespaceSelector()
        {
            var asf = It.IsAny<string>();
            var sut = new AssemblySourceFilter(New.Cqrs().AssemblyFilterSourceProvider.Creation, asf, It.IsAny<IEnumerable<Assembly>>());
            Assert.Equal(asf, sut.NamespaceSelector);
        }

        [Fact]
        public void MustSetExplicitAssemblies()
        {
            var asf = new Mock<List<Assembly>>();
            var sut = new AssemblySourceFilter(New.Cqrs().AssemblyFilterSourceProvider.Creation, It.IsAny<string>(), asf.Object);
            Assert.Equal(asf.Object, sut.ExplictAssemblies);
        }

        [Fact]
        public void MustSetExplicitAssembliesToNewListOfAssembliesIfNull()
        {
            var sut = new AssemblySourceFilter(New.Cqrs().AssemblyFilterSourceProvider.Creation, It.IsAny<string>(), null);
            Assert.Equal(new List<Assembly>(), sut.ExplictAssemblies);
        }
    }
}
