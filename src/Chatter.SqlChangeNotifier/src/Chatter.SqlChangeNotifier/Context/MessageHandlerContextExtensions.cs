using Chatter.CQRS.Events;
using Chatter.MessageBrokers.SqlServiceBroker.Context;

namespace Chatter.CQRS.Context
{
    public static class MessageHandlerContextExtensions
    {
        public static SqlChangeNotificationContext<TEvent> ChangeNotificationContext<TEvent>(this IMessageHandlerContext messageHandlerContext) where TEvent : class, IEvent
        {
            if (messageHandlerContext.Container.TryGet<SqlChangeNotificationContext<TEvent>>(out var context))
            {
                return context;
            }

            return null;
        }
    }
}
