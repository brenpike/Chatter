using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Moq;
using System;
using System.Collections.Generic;
using System.Text;

namespace Chatter.Testing.Core.Creators.CQRS
{
    public class CommandBehaviorCreator : Creator<ICommandBehavior<IMessage>>
    {
        private readonly Mock<ICommandBehavior<IMessage>> _commandBehavior = new Mock<ICommandBehavior<IMessage>>();

        public CommandBehaviorCreator(INewContext newContext, ICommandBehavior<IMessage> creation = null) 
            : base(newContext, creation)
        {
            _commandBehavior.Setup(cb => cb.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>(), It.IsAny<CommandHandlerDelegate>()));
            Creation = _commandBehavior.Object;
        }

        public CommandBehaviorCreator ThatWrapsCommandHandler(CommandHandlerDelegate @delegate)
        {
            _commandBehavior.Setup(cb => cb.Handle(It.IsAny<IMessage>(), It.IsAny<IMessageHandlerContext>(), @delegate));
            return this;
        }
    }
}
