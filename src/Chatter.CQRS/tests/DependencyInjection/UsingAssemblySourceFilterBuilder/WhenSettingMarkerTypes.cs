using Chatter.CQRS.DependencyInjection;
using FluentAssertions;
using Moq;
using System;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingAssemblySourceFilterBuilder
{
    public class WhenSettingMarkerTypes
    {
        private readonly AssemblySourceFilterBuilder _sut;

        public WhenSettingMarkerTypes() => _sut = AssemblySourceFilterBuilder.New();

        [Fact]
        public void MustReturnBuilder()
        {
            var retVal = _sut.WithMarkerTypes(It.IsAny<Type>());
            Assert.IsType<AssemblySourceFilterBuilder>(retVal);
            Assert.Same(retVal, _sut);
        }

        [Fact]
        public void MustSetExplicitAssembliesViaMarkerTypes()
        {
            var type = typeof(CQRS.Commands.ICommand);

            var filter = _sut.WithMarkerTypes(type).Build();
            filter.ExplictAssemblies.Should().Contain(type.Assembly);
            filter.ExplictAssemblies.Should().HaveCount(1);
        }

        [Fact]
        public void MustSetDistinctExplicitAssembliesViaMarkerTypes()
        {
            var type = typeof(CQRS.Commands.ICommand);
            var type2 = typeof(CQRS.Events.IEvent);
            var type3 = typeof(MessageBrokers.Sending.IBrokeredMessageDispatcher);

            var filter = _sut.WithMarkerTypes(type, type2, type3).Build();
            filter.ExplictAssemblies.Should().Contain(type.Assembly);
            filter.ExplictAssemblies.Should().Contain(type3.Assembly);
            filter.ExplictAssemblies.Should().HaveCount(2);
        }

        [Fact]
        public void MustSelectNoExplicitAssembliesIfProvidedNullMarkerTypes()
        {
            FluentActions.Invoking(() => _sut.WithMarkerTypes(null)).Should().NotThrow();
            var filter = _sut.WithMarkerTypes(null).Build();
            filter.ExplictAssemblies.Should().BeEmpty();
        }
    }
}
