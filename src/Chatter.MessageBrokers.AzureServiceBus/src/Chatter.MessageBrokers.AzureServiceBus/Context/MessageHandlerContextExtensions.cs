using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Context;

namespace Chatter.CQRS.Context
{
    public static class MessageHandlerContextExtensions
    {
        public static IAzureServiceBusContextDispatcher AzureServiceBus(this IMessageHandlerContext context)
        {
            if (context is IMessageBrokerContext mbc)
            {
                return new AzureServiceBusContextDispatcher(mbc);
            }

            return null;
        }
    }
}
