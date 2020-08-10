using Chatter.CQRS;

namespace Chatter.MessageBrokers.Sending
{
    public class DispatchContext
    {
        public DispatchContext(IMessageDispatcher internalDispatcher, IBrokeredMessageDispatcher externalDispatcher)
        {
            InternalDispatcher = internalDispatcher;
            ExternalDispatcher = externalDispatcher;
        }

        public IMessageDispatcher InternalDispatcher { get; }
        public IBrokeredMessageDispatcher ExternalDispatcher { get; }
    }
}
