using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingChatterBuilder
{
    public class WhenGettingProperties
    {
        private readonly Mock<IServiceCollection> _serviceCollection;
        private readonly Mock<IConfiguration> _configuration;
        private readonly Mock<IAssemblySourceFilter> _assemblySourceFilterMock;
        private readonly IChatterBuilder _sut;

        public WhenGettingProperties()
        {
            _serviceCollection = new Mock<IServiceCollection>();
            _configuration = new Mock<IConfiguration>();
            _assemblySourceFilterMock = new Mock<IAssemblySourceFilter>();
            _sut = ChatterBuilder.Create(_serviceCollection.Object, _configuration.Object, _assemblySourceFilterMock.Object);
        }

        [Fact]
        public void MustGetServiceCollection()
            => _sut.Services.Should().NotBeNull().And.BeSameAs(_serviceCollection.Object);

        [Fact]
        public void MustGetConfiguration()
            => _sut.Configuration.Should().NotBeNull().And.BeSameAs(_configuration.Object);

        [Fact]
        public void MustGetMarkerAssemblies()
            => _sut.AssemblySourceFilter.Should().NotBeNull().And.BeSameAs(_assemblySourceFilterMock.Object);
    }
}
