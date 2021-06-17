using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingServiceCollectionExtensions
{
    public class WhenRegisteringCommandBehaviorForCommand
    {
        public WhenRegisteringCommandBehaviorForCommand() { }

        [Fact]
        public void MustThrowWhenTypeIsOpenGeneric()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(() => sc.RegisterBehaviorForCommand(typeof(FakeCommandBehavior<>))).Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void MustThrowWhenTypeIsClosedGenericWithMultipleGenericTypeParameters()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(() => sc.RegisterBehaviorForCommand(typeof(FakeCommandBehavior2<FakeCommand, FakeCommand>))).Should().Throw<Exception>();
        }

        [Fact]
        public void MustThrowWhenTypeIsNotICommandBehavior()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(() => sc.RegisterBehaviorForCommand(typeof(FakeCommandBehavior3<NotACommand>))).Should().ThrowExactly<ArgumentException>();
        }

        [Fact]
        public void MustThrowWhenTypeIsNull()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(() => sc.RegisterBehaviorForCommand(null)).Should().ThrowExactly<ArgumentNullException>();
        }

        [Fact]
        public void MustRegisterICommandBehaviorForCommandTypeWithNewICommandBehaviorImplementation()
        {
            var sc = new ServiceCollection();
            sc.AddTransient<ICommandBehavior<FakeCommand>, AnotherCommandBehavior<FakeCommand>>();
            sc.AddTransient<ICommandBehavior<FakeCommand>, YetAnotherCommandBehavior<FakeCommand>>();

            sc[0].ServiceType.Should().Be(typeof(ICommandBehavior<FakeCommand>));
            sc[0].ImplementationType.Should().Be(typeof(AnotherCommandBehavior<FakeCommand>));
            sc[1].ServiceType.Should().Be(typeof(ICommandBehavior<FakeCommand>));
            sc[1].ImplementationType.Should().Be(typeof(YetAnotherCommandBehavior<FakeCommand>));
            sc.Should().HaveCount(2);

            sc.RegisterBehaviorForCommand(typeof(FakeCommandBehavior<FakeCommand>));

            sc[2].ServiceType.Should().Be(typeof(ICommandBehavior<FakeCommand>));
            sc[2].ImplementationType.Should().Be(typeof(FakeCommandBehavior<FakeCommand>));
            sc.Should().HaveCount(3);
        }

        private class NotACommand { }
        private class FakeCommand : ICommand { }
        private class AnotherFakeCommand : ICommand { }
        private class FakeCommandBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
        private class FakeCommandBehavior2<TMessage, TFake> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
        private class FakeCommandBehavior3<TMessage> { }
        private class AnotherCommandBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
        private class YetAnotherCommandBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
    }
}
