using System.Threading;

namespace Chatter.CQRS.Context
{
    /// <summary>
    /// Context that is passed to <see cref="IMessageHandler{TMessage}"/> when an <see cref="IMessage"/> is handled.
    /// </summary>
    public class MessageHandlerContext : IMessageHandlerContext
    {
        public MessageHandlerContext(CancellationToken cancellationToken)
        {
            Container = new ContextContainer();
            CancellationToken = cancellationToken;
        }

        public MessageHandlerContext()
            : this(default)
        { }

        /// <summary>
        /// A context container that support extensibility by holding additional context
        /// </summary>
        public ContextContainer Container { get; private set; }

        public CancellationToken CancellationToken { get; private set; }
    }
}
