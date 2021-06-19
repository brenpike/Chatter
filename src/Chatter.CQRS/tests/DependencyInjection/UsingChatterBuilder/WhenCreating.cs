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
    public class WhenCreating
    {
        private readonly Mock<IServiceCollection> _serviceCollection;
        private readonly Mock<IConfiguration> _configuration;
        private readonly Mock<IEnumerable<Assembly>> _markerAssemblies;

        public WhenCreating()
        {
            _serviceCollection = new Mock<IServiceCollection>();
            _configuration = new Mock<IConfiguration>();
            _markerAssemblies = new Mock<IEnumerable<Assembly>>();
        }

        [Fact]
        public void MustReturnNewChatterBuilderInstance()
            => FluentActions.Invoking(()
                => ChatterBuilder.Create(_serviceCollection.Object, _configuration.Object, _markerAssemblies.Object))
            .Should().NotThrow().And.NotBeNull().And.BeOfType(typeof(IChatterBuilder));
    }
}
