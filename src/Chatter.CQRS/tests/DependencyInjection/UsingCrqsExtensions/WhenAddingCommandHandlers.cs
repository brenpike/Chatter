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
    public class WhenAddingCommandHandlers : Testing.Core.Context
    {
        [Fact]
        public void MustRegisterGenericHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeGenericCommandHandler<>)).Creation;
            var sc = new ServiceCollection();
            sc.AddCommandHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeGenericCommandHandler<>));

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeCommand>));
            sd.ImplementationType.Should().Be(typeof(FakeGenericCommandHandler<>));
        }

        [Fact]
        public void MustRegisterHandlersForGenericCommand()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCommandHandler3)).Creation;
            var sc = new ServiceCollection();
            sc.AddCommandHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeCommandHandler3));

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeGenericCommand<string>>));
            sd.ImplementationType.Should().Be(typeof(FakeCommandHandler3));
        }

        [Fact]
        public void MustReplaceDuplicateRegistrations()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCommandHandler), typeof(FakeCommandHandler2)).Creation;
            var sc = new ServiceCollection();
            sc.AddCommandHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeCommandHandler2));

            sc.Should().HaveCount(1);

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeCommand>));
            sd.ImplementationType.Should().Be(typeof(FakeCommandHandler2));
        }

        [Fact]
        public void MustOnlyRegisterCommandHandlers()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCommandHandler), typeof(FakeEventHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddCommandHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeCommandHandler));

            sc.Should().HaveCount(1);

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeCommand>));
            sd.ImplementationType.Should().Be(typeof(FakeCommandHandler));
        }

        [Fact]
        public void MustReturnSelf()
        {
            var sc = new ServiceCollection();
            var returnValue = sc.AddCommandHandlers(new Assembly[] { });
            returnValue.Should().BeSameAs(sc);
        }

        private class FakeGenericCommandHandler<T> : IMessageHandler<FakeCommand>
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeGenericCommand<T> : ICommand { }
        private class FakeCommandHandler3 : IMessageHandler<FakeGenericCommand<string>>
        {
            public Task Handle(FakeGenericCommand<string> message, IMessageHandlerContext context) => throw new NotImplementedException();
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

        private class FakeCommandHandler2 : IMessageHandler<FakeCommand>
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }
    }
}
