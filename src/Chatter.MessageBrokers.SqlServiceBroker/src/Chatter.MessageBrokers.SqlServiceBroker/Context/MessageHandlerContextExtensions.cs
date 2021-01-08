using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.SqlServiceBroker.Sending;

namespace Chatter.CQRS.Context
{
    public static class MessageHandlerContextExtensions
    {
        public static ISqlServiceBrokerContextDispatcher SqlServiceBroker(this IMessageHandlerContext context)
        {
            if (context is IMessageBrokerContext mbc)
            {
                return new SqlServiceBrokerContextDispatcher(mbc);
            }

            return null;
        }
    }
}
