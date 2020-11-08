using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using System.Linq;
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

        public static Task Forward(this IMessageBrokerContext context, RoutingSlip slip)
        {
            var destination = slip.Route.FirstOrDefault()?.DestinationPath;
            if (!string.IsNullOrWhiteSpace(destination))
            {
                return context.BrokeredMessageDispatcher.Forward(context.BrokeredMessage, slip, context.GetTransactionContext());
            }

            return Task.CompletedTask;
        }
    }
}
