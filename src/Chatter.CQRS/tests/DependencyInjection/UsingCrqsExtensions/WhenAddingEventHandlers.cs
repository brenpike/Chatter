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
    public class WhenAddingEventHandlers : Testing.Core.Context
    {
        [Fact]
        public void MustRegisterEventHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeEventHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddEventHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeEventHandler));

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeEvent>));
            sd.ImplementationType.Should().Be(typeof(FakeEventHandler));
        }

        //[Fact]
        //public void MustRegisterGenericHandler()
        //{
        //    var assembly = New.Common().Assembly.WithTypes(typeof(FakeGenericEventHandler<string>)).Creation;
        //    var sc = new ServiceCollection();
        //    sc.AddEventHandlers(new Assembly[] { assembly });

        //    var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeGenericEventHandler<string>));

        //    sd.Lifetime.Should().Be(ServiceLifetime.Transient);
        //    sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeEvent>));
        //    sd.ImplementationType.Should().Be(typeof(FakeGenericEventHandler<string>));
        //}

        [Fact]
        public void MustRegisterHandlersForGenericEvent()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeEventHandler3)).Creation;
            var sc = new ServiceCollection();
            sc.AddEventHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeEventHandler3));

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeGenericEvent<string>>));
            sd.ImplementationType.Should().Be(typeof(FakeEventHandler3));
        }

        [Fact]
        public void MustNotReplaceDuplicateRegistrations()
        {
            var assembly = New.Common().Assembly
                .WithTypes(typeof(FakeEventHandler), typeof(FakeEventHandler2))
                .Creation;
            var sc = new ServiceCollection();
            sc.AddEventHandlers(new Assembly[] { assembly });

            sc.Should().HaveCount(2);
            sc.Should().OnlyHaveUniqueItems();

            sc[0].Lifetime.Should().Be(ServiceLifetime.Transient);
            sc[0].ServiceType.Should().Be(typeof(IMessageHandler<FakeEvent>));
            sc[0].ImplementationType.Should().Be(typeof(FakeEventHandler));

            sc[1].Lifetime.Should().Be(ServiceLifetime.Transient);
            sc[1].ServiceType.Should().Be(typeof(IMessageHandler<FakeEvent>));
            sc[1].ImplementationType.Should().Be(typeof(FakeEventHandler2));
        }

        [Fact]
        public void MustOnlyRegisterEventHandlers()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCommandHandler), typeof(FakeEventHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddEventHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeEventHandler));

            sc.Should().HaveCount(1);

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeEvent>));
            sd.ImplementationType.Should().Be(typeof(FakeEventHandler));
        }

        [Fact]
        public void MustReturnSelf()
        {
            var sc = new ServiceCollection();
            var returnValue = sc.AddEventHandlers(new Assembly[] { });
            returnValue.Should().BeSameAs(sc);
        }

        private class FakeGenericEventHandler<T> : IMessageHandler<FakeEvent>
        {
            public Task Handle(FakeEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeGenericEvent<T> : IEvent { }
        private class FakeEventHandler3 : IMessageHandler<FakeGenericEvent<string>>
        {
            public Task Handle(FakeGenericEvent<string> message, IMessageHandlerContext context) => throw new NotImplementedException();
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

        private class FakeEventHandler2 : IMessageHandler<FakeEvent>
        {
            public Task Handle(FakeEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }
    }
}
