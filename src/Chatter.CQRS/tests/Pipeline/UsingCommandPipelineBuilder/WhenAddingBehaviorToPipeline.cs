using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Pipeline.UsingCommandPipelineBuilder
{
    public class WhenAddingBehaviorToPipeline
    {
        private ServiceCollection _serviceCollection;
        private CommandPipelineBuilder _commandPipelineBuilder;

        public class NotACommandBehavior { }
        public class FakeCommand : ICommand { }
        public class FakeCommandBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }

        public WhenAddingBehaviorToPipeline()
        {
            _serviceCollection = new ServiceCollection();
            _commandPipelineBuilder = new CommandPipelineBuilder(_serviceCollection);
        }

        [Fact]
        public void MustNotAddTypeToCommandBehaviorPipelineIfTypeIsNotICommandBehavior()
        {
            var type = new Mock<NotACommandBehavior>();
            FluentActions.Invoking(() => _commandPipelineBuilder.WithBehavior<NotACommandBehavior>()).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => _commandPipelineBuilder.WithBehavior(typeof(NotACommandBehavior))).Should().Throw<ArgumentException>();
            Assert.Empty(_serviceCollection);
        }

        //[Fact]
        //public void MustAddTypeToCommandBehaviorPipelineIfTypeIsICommandBehavior()
        //{
            //_commandPipelineBuilder.WithBehavior<FakeCommandBehavior<FakeCommand>>();
            //Assert.Single(_serviceCollection);
            //Assert.Collection(_serviceCollection, item =>
            //{
            //    Assert.Equal(typeof(ICommandBehavior<FakeCommand>), item.ServiceType);
            //    Assert.Equal(typeof(FakeCommandBehavior<FakeCommand>), item.ImplementationType);
            //});
        //}
    }
}
