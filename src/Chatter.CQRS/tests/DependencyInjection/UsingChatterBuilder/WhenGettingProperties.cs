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
        private readonly Mock<IEnumerable<Assembly>> _markerAssemblies;
        private readonly IChatterBuilder _sut;

        public WhenGettingProperties()
        {
            _serviceCollection = new Mock<IServiceCollection>();
            _configuration = new Mock<IConfiguration>();
            _markerAssemblies = new Mock<IEnumerable<Assembly>>();
            _sut = ChatterBuilder.Create(_serviceCollection.Object, _configuration.Object, _markerAssemblies.Object);
        }

        [Fact]
        public void MustGetServiceCollection()
            => _sut.Services.Should().NotBeNull().And.BeSameAs(_serviceCollection.Object);

        [Fact]
        public void MustGetConfiguration()
            => _sut.Configuration.Should().NotBeNull().And.BeSameAs(_configuration.Object);

        [Fact]
        public void MustGetMarkerAssemblies()
            => _sut.MarkerAssemblies.Should().NotBeNull().And.BeSameAs(_markerAssemblies.Object);
    }
}
