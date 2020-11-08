using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Context
{
    public static class MessageBrokerContextExtensions
    {
        public static Task Forward(this IMessageBrokerContext context, string forwardDestination) 
            => context.BrokeredMessageDispatcher.Forward(context.BrokeredMessage, forwardDestination, context.GetTransactionContext());
    }
}
