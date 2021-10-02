using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingChatterBuilder
{
    public class WhenInitializing
    {
        private readonly Mock<IServiceCollection> _serviceCollection;
        private readonly Mock<IConfiguration> _configuration;
        private readonly Mock<IAssemblySourceFilter> _assemblySourceFilter;

        public WhenInitializing()
        {
            _serviceCollection = new Mock<IServiceCollection>();
            _configuration = new Mock<IConfiguration>();
            _assemblySourceFilter = new Mock<IAssemblySourceFilter>();
        }

        [Fact]
        public void MustThrowWhenServiceCollectionIsNull()
            => FluentActions.Invoking(()
                => ChatterBuilder.Create(null, _configuration.Object, _assemblySourceFilter.Object))
            .Should()
            .ThrowExactly<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenConfigurationIsNull()
            => FluentActions.Invoking(()
                => ChatterBuilder.Create(_serviceCollection.Object, null, _assemblySourceFilter.Object))
            .Should()
            .ThrowExactly<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenAssemblySourceFilterIsNull()
            => FluentActions.Invoking(()
                => ChatterBuilder.Create(_serviceCollection.Object, _configuration.Object, null))
            .Should()
            .ThrowExactly<ArgumentNullException>();
    }
}
