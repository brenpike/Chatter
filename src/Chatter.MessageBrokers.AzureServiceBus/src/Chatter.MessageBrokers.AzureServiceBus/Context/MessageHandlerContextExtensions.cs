using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;

namespace Chatter.CQRS.Context
{
    public static class MessageHandlerContextExtensions
    {
        public static IMessageBrokerContext AzureServiceBus(this IMessageHandlerContext context)
        {
            if (context is IMessageBrokerContext mbc)
            {
                mbc.BrokeredMessage?.UseMessagingInfrastructure(it => it.AzureServiceBus());
                return mbc;
            }

            return null;
        }
    }
}
