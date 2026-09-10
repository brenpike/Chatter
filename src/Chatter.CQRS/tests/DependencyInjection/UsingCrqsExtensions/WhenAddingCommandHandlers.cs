using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenAddingCommandHandlers : Testing.Core.Context
    {
        [Fact]
        public void MustRegisterCommandHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCommandHandler)).Creation;
            var sc = new ServiceCollection();
            sc.AddCommandHandlers(new Assembly[] { assembly });

            var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeCommandHandler));

            sd.Lifetime.Should().Be(ServiceLifetime.Transient);
            sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeCommand>));
            sd.ImplementationType.Should().Be(typeof(FakeCommandHandler));
        }

        //[Fact]
        //public void MustRegisterGenericHandler()
        //{
        //    var assembly = New.Common().Assembly.WithTypes(typeof(FakeGenericCommandHandler<string>)).Creation;
        //    var sc = new ServiceCollection();
        //    sc.AddCommandHandlers(new Assembly[] { assembly });

        //    var sd = sc.GetServiceDescriptorByImplementationType(typeof(FakeGenericCommandHandler<string>));

        //    sd.Lifetime.Should().Be(ServiceLifetime.Transient);
        //    sd.ServiceType.Should().Be(typeof(IMessageHandler<FakeCommand>));
        //    sd.ImplementationType.Should().Be(typeof(FakeGenericCommandHandler<string>));
        //}

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
        public void MustNotRegisterNonHandlerInterfacesOfACommandHandler()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCommandHandlerWithService)).Creation;
            var sc = new ServiceCollection();
            sc.AddCommandHandlers(new Assembly[] { assembly });

            sc.Should().NotContain(sd => sd.ServiceType == typeof(IFakeService));
            sc.Should().Contain(sd => sd.ServiceType == typeof(IMessageHandler<FakeCommand>)
                                      && sd.ImplementationType == typeof(FakeCommandHandlerWithService));
        }

        [Fact]
        public void MustNotReplaceAnUnrelatedRegistrationMadeBeforeTheScan()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeCommandHandlerWithService)).Creation;
            var sc = new ServiceCollection();
            sc.AddScoped<IFakeService, FakeCommandHandlerWithService>();

            sc.AddCommandHandlers(new Assembly[] { assembly });

            var sd = sc.Single(d => d.ServiceType == typeof(IFakeService));
            sd.Lifetime.Should().Be(ServiceLifetime.Scoped);
            sd.ImplementationType.Should().Be(typeof(FakeCommandHandlerWithService));
        }

        [Fact]
        public void MustRegisterOneDescriptorPerCommandHandledByAHandlerOfTwoCommands()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeHandlerOfTwoCommands)).Creation;
            var sc = new ServiceCollection();
            sc.AddCommandHandlers(new Assembly[] { assembly });

            sc.Count(sd => sd.ServiceType == typeof(IMessageHandler<FakeCommand>)
                           && sd.ImplementationType == typeof(FakeHandlerOfTwoCommands)).Should().Be(1);
            sc.Count(sd => sd.ServiceType == typeof(IMessageHandler<FakeOtherCommand>)
                           && sd.ImplementationType == typeof(FakeHandlerOfTwoCommands)).Should().Be(1);
        }

        [Fact]
        public void MustReturnSelf()
        {
            var sc = new ServiceCollection();
            var returnValue = sc.AddCommandHandlers(new Assembly[] { });
            returnValue.Should().BeSameAs(sc);
        }

        private interface IFakeService { }
        private class FakeCommandHandlerWithService : IMessageHandler<FakeCommand>, IFakeService
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeOtherCommand : ICommand { }
        private class FakeHandlerOfTwoCommands : IMessageHandler<FakeCommand>, IMessageHandler<FakeOtherCommand>
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
            public Task Handle(FakeOtherCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
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
