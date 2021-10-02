using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingMessageHandlers : Testing.Core.Context
    {
        [Fact]
        public void MustRegisterEventHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeEventHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddMessageHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeEventHandler));

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeEvent>));
            sd.ImplementationType.Should().Be(typeof(FakeEventHandler));
        }

        [Fact]
        public void MustRegisterCommandHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCommandHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddMessageHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeCommandHandler));

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeCommand>));
            sd.ImplementationType.Should().Be(typeof(FakeCommandHandler));
        }

        [Fact]
        public void MustReturnSelf()
        {
            var sc = new ServiceCollection();
            var returnValue = sc.AddMessageHandlers(new Assembly[] { });
            returnValue.Should().BeSameAs(sc);
        }

        private class FakeEvent : IEvent { }
        private class FakeEventHandler : IMessageHandler<FakeEvent>
        {
            public Task Handle(FakeEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeCommand : ICommand { }
        private class FakeCommandHandler : IMessageHandler<FakeCommand>
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }
    }
}
