using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Moq;

namespace Chatter.Testing.Core.Creators.CQRS
{
    public class CommandBehaviorCreator : Creator<ICommandBehavior<ICommand>>
    {
        private readonly Mock<ICommandBehavior<ICommand>> _commandBehavior = new Mock<ICommandBehavior<ICommand>>();

        public CommandBehaviorCreator(INewContext newContext, ICommandBehavior<ICommand> creation = null)
            : base(newContext, creation)
        {
            _commandBehavior.Setup(cb => cb.Handle(It.IsAny<ICommand>(), It.IsAny<IMessageHandlerContext>(), It.IsAny<CommandHandlerDelegate>()));
            Creation = _commandBehavior.Object;
        }

        public CommandBehaviorCreator ThatWrapsCommandHandler(CommandHandlerDelegate @delegate)
        {
            _commandBehavior.Setup(cb => cb.Handle(It.IsAny<ICommand>(), It.IsAny<IMessageHandlerContext>(), @delegate));
            return this;
        }
    }
}
