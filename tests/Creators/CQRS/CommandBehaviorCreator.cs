using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Moq;
using System;
using System.Threading.Tasks;

namespace Chatter.Testing.Core.Creators.CQRS
{
    public class CommandBehaviorCreator<TCommand> : Creator<ICommandBehavior<TCommand>> where TCommand : ICommand
    {
        public CommandBehaviorCreator(INewContext newContext, ICommandBehavior<TCommand> creation = null)
            : base(newContext, creation)
        {
            Creation = new Mock<ICommandBehavior<TCommand>>().Object;

        }

        private class StandardCommandBehavior1<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }

        private class StandardCommandBehavior2<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }

        private class StandardCommandBehavior3<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }

        private class CommandBehaviorWithMultipleTypeParams<TMessage, TFake> : ICommandBehavior<TMessage> where TMessage : ICommand
        {
            public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) => throw new NotImplementedException();
        }

        private class FakeCommandBehavior3<TMessage> { }
    }
}
