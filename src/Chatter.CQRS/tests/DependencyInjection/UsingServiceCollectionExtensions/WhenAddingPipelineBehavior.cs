using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.DependencyInjection.UsingServiceCollectionExtensions
{
    public class WhenAddingPipelineBehavior
    {
        public WhenAddingPipelineBehavior() { }

        [Fact]
        public void MustThrowWhenAddingNullCommandPipelineBehaviorType()
        {
            var sc = new ServiceCollection();
            FluentActions.Invoking(
                () => sc.AddPipelineBehavior(null)
            ).Should()
            .ThrowExactly<ArgumentNullException>()
            .WithMessage($"Cannot add null behavior type to command pipeline.*");
        }

        [Fact]
        public void MustThrowWhenImplementedInterfacesOfCommandPipelineBehaviorTypeReturnsNull()
        {
            var sc = new ServiceCollection();
            var type = new Mock<TypeInfo>();
            var typeName = "typeName";
            type.SetupGet(t => t.ImplementedInterfaces).Returns(value: null);
            type.SetupGet(t => t.Name).Returns("typeName");
            FluentActions.Invoking(
                () => sc.AddPipelineBehavior(type.Object)
            ).Should()
            .ThrowExactly<NullReferenceException>()
            .WithMessage($"Unable to get implemented interfaces for '{typeName}'*");
        }

        [Fact]
        public void MustThrowWhenCommandBehaviorPipelineTypeImplementsGenericTypeThatIsntICommandBehavior()
        {
            var sc = new ServiceCollection();
            var type = new Mock<Type>();
            type.SetupGet(t => t.IsGenericType).Returns(true);
            type.Setup(t => t.GetGenericTypeDefinition()).Returns(value: It.IsAny<Type>());
            FluentActions.Invoking(
                () => sc.AddPipelineBehavior(type.Object)
            ).Should()
            .ThrowExactly<ArgumentException>()
            .WithMessage($"The supplied type must implement {typeof(ICommandBehavior<>).Name}*");
        }

        [Fact]
        public void MustThrowWhenCommandBehaviorPipelineTypeDoesntImplementedGenericType()
        {
            var sc = new ServiceCollection();
            var type = new Mock<Type>();
            type.SetupGet(t => t.IsGenericType).Returns(false);
            type.Setup(t => t.GetGenericTypeDefinition()).Returns(value: It.IsAny<Type>());
            FluentActions.Invoking(
                () => sc.AddPipelineBehavior(type.Object)
            ).Should()
            .ThrowExactly<ArgumentException>()
            .WithMessage($"The supplied type must implement {typeof(ICommandBehavior<>).Name}*");
        }

        [Fact]
        public void MustRegisterCommandPipelineBehaviorForAllCommandsIfTypeIsGenericTypeDefinition()
        {
            var sc = new ServiceCollection();
            sc.AddPipelineBehavior(typeof(FakeCommandBehavior<>));
            sc.Should().HaveCount(1);
            sc[0].ServiceType.Should().Be(typeof(ICommandBehavior<>));
            sc[0].ImplementationType.Should().Be(typeof(FakeCommandBehavior<>));

            var sp = sc.BuildServiceProvider();
            var cb1 = sp.GetServices<ICommandBehavior<FakeCommand>>();
            var cb2 = sp.GetServices<ICommandBehavior<AnotherFakeCommand>>();

            cb1.Should().HaveCount(1);
            cb1.Should().ContainItemsAssignableTo<FakeCommandBehavior<FakeCommand>>();
            cb2.Should().HaveCount(1);
            cb2.Should().ContainItemsAssignableTo<FakeCommandBehavior<AnotherFakeCommand>>();
        }

        [Fact]
        public void MustRegisterCommandPipelineBehaviorForSpecificCommandTypeIfCommandTypeClosesICommandBehavior()
        {
            var sc = new ServiceCollection();
            sc.AddPipelineBehavior(typeof(FakeCommandBehavior<FakeCommand>));

            sc[0].ServiceType.Should().Be(typeof(ICommandBehavior<FakeCommand>));
            sc[0].ImplementationType.Should().Be(typeof(FakeCommandBehavior<FakeCommand>));
            sc.Should().HaveCount(1);
        }

        private class FakeCommand : ICommand { }
        private class AnotherFakeCommand : ICommand { }
        private class FakeCommandBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }
    }
}
