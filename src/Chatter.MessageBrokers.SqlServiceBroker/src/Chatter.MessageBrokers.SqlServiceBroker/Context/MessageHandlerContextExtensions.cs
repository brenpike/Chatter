using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;

namespace Chatter.CQRS.Context
{
    public static class MessageHandlerContextExtensions
    {
        public static IMessageBrokerContext SqlServiceBroker(this IMessageHandlerContext context)
        {
            if (context is IMessageBrokerContext mbc)
            {
                mbc.BrokeredMessage?.UseMessagingInfrastructure(it => it.SqlServiceBroker());
                return mbc;
            }

            return null;
        }
    }
}
