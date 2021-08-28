using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Moq;
using System.Reflection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingAssemblySourceFilterBuilder
{
    public class WhenSettingExplicitAssemblies
    {
        private readonly AssemblySourceFilterBuilder _sut;

        public WhenSettingExplicitAssemblies() => _sut = AssemblySourceFilterBuilder.New();

        [Fact]
        public void MustReturnBuilder()
        {
            var retVal = _sut.WithExplicitAssemblies(It.IsAny<Assembly>());
            Assert.IsType<AssemblySourceFilterBuilder>(retVal);
            Assert.Same(retVal, _sut);
        }

        [Fact]
        public void MustSetExplicitAssembliesViaMarkerTypes()
        {
            var assembly = typeof(CQRS.Commands.ICommand).Assembly;

            var filter = _sut.WithExplicitAssemblies(assembly).Build();
            filter.ExplictAssemblies.Should().Contain(assembly);
            filter.ExplictAssemblies.Should().HaveCount(1);
        }

        [Fact]
        public void MustSetDistinctExplicitAssembliesViaMarkerTypes()
        {
            var assembly = typeof(CQRS.Commands.ICommand).Assembly;
            var assembly2 = typeof(CQRS.Events.IEvent).Assembly;
            var assembly3 = typeof(MessageBrokers.Sending.IBrokeredMessageDispatcher).Assembly;

            var filter = _sut.WithExplicitAssemblies(assembly, assembly2, assembly3).Build();
            filter.ExplictAssemblies.Should().Contain(assembly);
            filter.ExplictAssemblies.Should().Contain(assembly3);
            filter.ExplictAssemblies.Should().HaveCount(2);
        }
    }
}
