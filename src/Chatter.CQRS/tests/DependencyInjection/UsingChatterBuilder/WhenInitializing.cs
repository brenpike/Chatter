using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingChatterBuilder
{
    public class WhenInitializing
    {
        private readonly Mock<IServiceCollection> _serviceCollection;
        private readonly Mock<IConfiguration> _configuration;
        private readonly Mock<IEnumerable<Assembly>> _markerAssemblies;

        public WhenInitializing()
        {
            _serviceCollection = new Mock<IServiceCollection>();
            _configuration = new Mock<IConfiguration>();
            _markerAssemblies = new Mock<IEnumerable<Assembly>>();
        }

        [Fact]
        public void MustThrowWhenServiceCollectionIsNull()
            => FluentActions.Invoking(()
                => ChatterBuilder.Create(null, _configuration.Object, _markerAssemblies.Object))
            .Should()
            .ThrowExactly<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenConfigurationIsNull()
            => FluentActions.Invoking(()
                => ChatterBuilder.Create(_serviceCollection.Object, null, _markerAssemblies.Object))
            .Should()
            .ThrowExactly<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenEnumerableOfMarkerAssembliesIsNull()
            => FluentActions.Invoking(()
                => ChatterBuilder.Create(_serviceCollection.Object, _configuration.Object, null))
            .Should()
            .ThrowExactly<ArgumentNullException>();

        [Fact]
        public void MustNotThrowIfMarkerAssembliesAreEmpty()
        {
            _markerAssemblies.Setup(m => m.GetEnumerator()).Returns(new List<Assembly>().GetEnumerator());
            FluentActions.Invoking(()
                           => ChatterBuilder.Create(_serviceCollection.Object, _configuration.Object, _markerAssemblies.Object))
                       .Should()
                       .NotThrow();
        }
    }
}
