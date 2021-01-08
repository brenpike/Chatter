using Chatter.CQRS;
using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public class InMemoryDispatcher : IInMemoryDispatcher
    {
        private readonly IMessageHandlerContext _context;

        public InMemoryDispatcher(IMessageHandlerContext context) => _context = context;

        /// <inheritdoc/>
        public Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage
        {
            if (_context.Container.TryGet<IMessageDispatcher>(out var messageDispatcher))
            {
                return messageDispatcher.Dispatch(message, _context);
            }

            return Task.CompletedTask;
        }
    }
}
