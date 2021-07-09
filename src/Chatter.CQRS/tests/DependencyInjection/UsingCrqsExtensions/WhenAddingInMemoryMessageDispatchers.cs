using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingInMemoryMessageDispatchers
    {
        [Fact]
        public void MustAddScopedDispatchers()
        {
            var sc = new ServiceCollection();
            sc.AddInMemoryMessageDispatchers();

            sc.Should().HaveCount(3);
            sc[0].Lifetime.Should().Be(ServiceLifetime.Scoped);
            sc[0].ServiceType.Should().Be(typeof(IMessageDispatcher));
            sc[0].ImplementationType.Should().Be(typeof(MessageDispatcher));

            sc[1].Lifetime.Should().Be(ServiceLifetime.Scoped);
            sc[1].ServiceType.Should().Be(typeof(IDispatchMessages));
            sc[1].ImplementationType.Should().Be(typeof(CommandDispatcher));

            sc[2].Lifetime.Should().Be(ServiceLifetime.Scoped);
            sc[2].ServiceType.Should().Be(typeof(IDispatchMessages));
            sc[2].ImplementationType.Should().Be(typeof(EventDispatcher));
        }

        [Fact]
        public void MustReturnSelf()
        {
            var sc = new ServiceCollection();
            var returnValue = sc.AddInMemoryMessageDispatchers();
            returnValue.Should().BeSameAs(sc);
        }
    }
}
