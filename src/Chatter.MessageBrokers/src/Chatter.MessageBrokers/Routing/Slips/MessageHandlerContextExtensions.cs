using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
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
            if (context.TryGetExternalDispatcher(out var bmd))
            {
                return bmd.Send(message, slip, context.GetTransactionContext(), options);
            }
            return Task.CompletedTask;
        }

        public static Task Forward(this IMessageHandlerContext context, RoutingSlip slip)
        {
            if (context.TryGetExternalDispatcher(out var brokeredMessageDispatcher))
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
