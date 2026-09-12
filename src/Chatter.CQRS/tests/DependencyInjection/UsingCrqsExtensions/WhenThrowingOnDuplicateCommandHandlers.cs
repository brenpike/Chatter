using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingCrqsExtensions
{
    public class WhenThrowingOnDuplicateCommandHandlers : Testing.Core.Context
    {
        [Fact]
        public void MustThrowNamingTheCommandAndEveryCompetingHandler()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeFirstCommandHandler), typeof(FakeSecondCommandHandler));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers())
                         .Should().Throw<InvalidOperationException>()
                         .Which.Message.Should().Contain(typeof(FakeCommand).FullName)
                         .And.Contain(typeof(FakeFirstCommandHandler).FullName)
                         .And.Contain(typeof(FakeSecondCommandHandler).FullName);
        }

        [Fact]
        public void MustNotThrowWhenSeveralHandlersHandleTheSameEvent()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeEventHandler), typeof(FakeEventHandler2));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowWhenTheOnlyOtherHandlersOfACommandAreAbstractOrOpenGeneric()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeAbstractCommandHandler),
                                                        typeof(FakeConcreteCommandHandler),
                                                        typeof(FakeOpenGenericCommandHandler<>));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustReportEveryAmbiguousCommandInOneExceptionOrderedByFullName()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeSecondGenericCommandHandler),
                                                        typeof(FakeSecondCommandHandler),
                                                        typeof(FakeFirstGenericCommandHandler),
                                                        typeof(FakeFirstCommandHandler));

            var message = FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers())
                                       .Should().Throw<InvalidOperationException>()
                                       .Which.Message;

            message.Should().Contain(typeof(FakeCommand).FullName)
                   .And.Contain(typeof(FakeFirstCommandHandler).FullName)
                   .And.Contain(typeof(FakeSecondCommandHandler).FullName)
                   .And.Contain(typeof(FakeGenericCommand<string>).FullName)
                   .And.Contain(typeof(FakeFirstGenericCommandHandler).FullName)
                   .And.Contain(typeof(FakeSecondGenericCommandHandler).FullName);

            PositionOf(message, typeof(FakeCommand)).Should().BeLessThan(PositionOf(message, typeof(FakeGenericCommand<string>)));
            PositionOf(message, typeof(FakeFirstCommandHandler)).Should().BeLessThan(PositionOf(message, typeof(FakeSecondCommandHandler)));
            PositionOf(message, typeof(FakeFirstGenericCommandHandler)).Should().BeLessThan(PositionOf(message, typeof(FakeSecondGenericCommandHandler)));
        }

        [Fact]
        public void MustRegisterTheLastHandlerScannedWithoutThrowingWhenTheCheckIsNotEnabled()
        {
            var assembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler), typeof(FakeSecondCommandHandler)).Creation;
            var services = new ServiceCollection();

            FluentActions.Invoking(() => services.AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                                 messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly)))
                         .Should().NotThrow();

            services.Should().ContainSingle(sd => sd.ServiceType == typeof(IMessageHandler<FakeCommand>))
                    .Which.ImplementationType.Should().Be(typeof(FakeSecondCommandHandler));
        }

        [Fact]
        public void MustReturnTheSameBuilderWhenNoCommandIsHandledMoreThanOnce()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeFirstCommandHandler));

            chatterBuilder.ThrowOnDuplicateCommandHandlers().Should().BeSameAs(chatterBuilder);
        }

        [Fact]
        public void MustNotThrowWhenHandlersHandleDifferentCommands()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeFirstCommandHandler), typeof(FakeFirstGenericCommandHandler));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowForAHandlerOfBothACommandAndAnEvent()
        {
            var chatterBuilder = AddChatterCqrsScanning(typeof(FakeDualRoleHandler));

            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        [Fact]
        public void MustNotReportCompetingHandlersInAnAssemblyOutsideTheFilterSet()
        {
            var scannedAssembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler)).Creation;
            var unscannedAssembly = New.Common().Assembly.WithTypes(typeof(FakeFirstCommandHandler), typeof(FakeSecondCommandHandler)).Creation;
            var chatterBuilder = new ServiceCollection().AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                                        messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(scannedAssembly));

            chatterBuilder.AssemblySourceFilter.Apply().Should().NotContain(unscannedAssembly);
            FluentActions.Invoking(() => chatterBuilder.ThrowOnDuplicateCommandHandlers()).Should().NotThrow();
        }

        private static int PositionOf(string message, Type type)
            => message.IndexOf(type.FullName, StringComparison.Ordinal);

        private IChatterBuilder AddChatterCqrsScanning(params Type[] scannedTypes)
        {
            var assembly = New.Common().Assembly.WithTypes(scannedTypes).Creation;
            return new ServiceCollection().AddChatterCqrs(Mock.Of<IConfiguration>(),
                                                          messageHandlerSourceBuilder: b => b.WithExplicitAssemblies(assembly));
        }

        private class FakeCommand : ICommand { }
        private class FakeFirstCommandHandler : IMessageHandler<FakeCommand>
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeSecondCommandHandler : IMessageHandler<FakeCommand>
        {
            public Task Handle(FakeCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeGenericCommand<TPayload> : ICommand { }
        private class FakeFirstGenericCommandHandler : IMessageHandler<FakeGenericCommand<string>>
        {
            public Task Handle(FakeGenericCommand<string> message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeSecondGenericCommandHandler : IMessageHandler<FakeGenericCommand<string>>
        {
            public Task Handle(FakeGenericCommand<string> message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeSinglyHandledCommand : ICommand { }
        private abstract class FakeAbstractCommandHandler : IMessageHandler<FakeSinglyHandledCommand>
        {
            public abstract Task Handle(FakeSinglyHandledCommand message, IMessageHandlerContext context);
        }

        private class FakeConcreteCommandHandler : FakeAbstractCommandHandler
        {
            public override Task Handle(FakeSinglyHandledCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeOpenGenericCommandHandler<TUnused> : IMessageHandler<FakeSinglyHandledCommand>
        {
            public Task Handle(FakeSinglyHandledCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeEvent : IEvent { }
        private class FakeEventHandler : IMessageHandler<FakeEvent>
        {
            public Task Handle(FakeEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeEventHandler2 : IMessageHandler<FakeEvent>
        {
            public Task Handle(FakeEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }

        private class FakeDualRoleCommand : ICommand { }
        private class FakeDualRoleHandler : IMessageHandler<FakeDualRoleCommand>, IMessageHandler<FakeEvent>
        {
            public Task Handle(FakeDualRoleCommand message, IMessageHandlerContext context) => throw new NotImplementedException();
            public Task Handle(FakeEvent message, IMessageHandlerContext context) => throw new NotImplementedException();
        }
    }
}
