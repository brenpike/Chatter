using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingInMemoryMessageDispatchers
    {

        [Fact]
        public void MustAddScopedMessageDispatcherToServiceCollection()
        {
            var sc = new ServiceCollection();
            sc.AddInMemoryMessageDispatchers();

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(MessageDispatcher));

            sd.Lifetime.Should().Be(ServiceLifetime.Scoped);
            sd.ServiceType.Should().Be(typeof(IMessageDispatcher));
            sd.ImplementationType.Should().Be(typeof(MessageDispatcher));
        }

        [Fact]
        public void MustAddScopedCommandDispatcherToServiceCollection()
        {
            var sc = new ServiceCollection();
            sc.AddInMemoryMessageDispatchers();

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(CommandDispatcher));

            sd.Lifetime.Should().Be(ServiceLifetime.Scoped);
            sd.ServiceType.Should().Be(typeof(IDispatchMessages));
            sd.ImplementationType.Should().Be(typeof(CommandDispatcher));
        }

        [Fact]
        public void MustAddScopedEventDispatcherToServiceCollection()
        {
            var sc = new ServiceCollection();
            sc.AddInMemoryMessageDispatchers();

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(EventDispatcher));

            sd.Lifetime.Should().Be(ServiceLifetime.Scoped);
            sd.ServiceType.Should().Be(typeof(IDispatchMessages));
            sd.ImplementationType.Should().Be(typeof(EventDispatcher));
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
