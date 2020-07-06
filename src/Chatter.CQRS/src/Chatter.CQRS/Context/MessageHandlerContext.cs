namespace Chatter.CQRS.Context
{
    public class MessageHandlerContext : IMessageHandlerContext
    {
        public MessageHandlerContext()
            : this(new ContextContainer())
        { }

        public MessageHandlerContext(ContextContainer container)
        {
            Container = container;
        }

        public ContextContainer Container { get; private set; }
    }
}
