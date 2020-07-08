namespace Chatter.CQRS.Context
{
    /// <summary>
    /// Context that is passed to <see cref="IMessageHandler{TMessage}"/> when an <see cref="IMessage"/> is handled.
    /// </summary>
    public class MessageHandlerContext : IMessageHandlerContext
    {
        public MessageHandlerContext()
            : this(new ContextContainer())
        { }

        public MessageHandlerContext(ContextContainer container)
        {
            Container = container;
        }

        /// <summary>
        /// A context container that support extensibility by holding additional context
        /// </summary>
        public ContextContainer Container { get; private set; }
    }
}
