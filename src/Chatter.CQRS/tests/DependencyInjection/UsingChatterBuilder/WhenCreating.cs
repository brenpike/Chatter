using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingChatterBuilder
{
    public class WhenCreating
    {
        private readonly Mock<IServiceCollection> _serviceCollection;
        private readonly Mock<IConfiguration> _configuration;
        private readonly Mock<IAssemblySourceFilter> _assemblySourceFilterMock;

        public WhenCreating()
        {
            _serviceCollection = new Mock<IServiceCollection>();
            _configuration = new Mock<IConfiguration>();
            _assemblySourceFilterMock = new Mock<IAssemblySourceFilter>();
        }

        [Fact]
        public void MustReturnNewChatterBuilderInstance()
            => FluentActions.Invoking(()
                => ChatterBuilder.Create(_serviceCollection.Object, _configuration.Object, _assemblySourceFilterMock.Object)).Should().NotThrow();
    }
}
