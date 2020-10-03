using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class MessageBrokerContextExtensions
    {
        public static Task Send<TMessage>(this IMessageBrokerContext context,
                                          TMessage message,
                                          RoutingSlip slip,
                                          SendOptions options = null)
            where TMessage : ICommand 
            => context.BrokeredMessageDispatcher.Send(message, slip, context.GetTransactionContext(), options);
    }
}
