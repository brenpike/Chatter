using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class BrokeredMessageDispatcherExtensions
    {
        public static Task Send<TMessage>(this IBrokeredMessageDispatcher dispatcher,
                                          TMessage message,
                                          RoutingSlip slip,
                                          TransactionContext transactionContext = null,
                                          SendOptions options = null)
            where TMessage : ICommand
        {
            if (options == null)
            {
                options = new SendOptions();
            }

            var destination = slip.RouteToNextStep();

            options.WithRoutingSlip(slip);

            return dispatcher.Send(message, destination, transactionContext, options);
        }

        public static Task Forward(this IBrokeredMessageDispatcher dispatcher,
                                   InboundBrokeredMessage message,
                                   RoutingSlip slip,
                                   TransactionContext transactionContext)
        {
            var destination = slip.RouteToNextStep();

            message.WithRoutingSlip(slip);

            return dispatcher.Forward(message, destination, transactionContext);
        }
    }
}
