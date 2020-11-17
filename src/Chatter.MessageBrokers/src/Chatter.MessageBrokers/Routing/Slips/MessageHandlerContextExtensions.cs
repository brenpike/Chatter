using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class MessageHandlerContextExtensions
    {
        public static Task Send<TMessage>(this IMessageHandlerContext context,
                            TMessage message,
                            RoutingSlip slip,
                            SendOptions options = null) where TMessage : ICommand
        {
            if (context.ExternalDispatcher is IBrokeredMessageDispatcher brokeredMessageDispatcher)
            {
                return brokeredMessageDispatcher.Send(message, slip, context.GetTransactionContext(), options);
            }

            return Task.CompletedTask;
        }

        public static Task Forward(this IMessageHandlerContext context, RoutingSlip slip)
        {
            if (context.ExternalDispatcher is IBrokeredMessageDispatcher brokeredMessageDispatcher)
            {
                var destination = slip.Route.FirstOrDefault()?.DestinationPath;
                if (!string.IsNullOrWhiteSpace(destination))
                {
                    return brokeredMessageDispatcher.Forward(context.GetInboundBrokeredMessage(), slip, context.GetTransactionContext());
                }
            }

            return Task.CompletedTask;
        }
    }
}
