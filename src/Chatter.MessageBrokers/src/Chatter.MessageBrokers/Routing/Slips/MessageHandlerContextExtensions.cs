using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class MessageHandlerContextExtensions
    {
        public static void AddRoutingSlip(this IMessageHandlerContext mhc, RoutingSlip routingSlip)
        {
            mhc.Container.Include(routingSlip);
        }

        /// <summary>
        /// Gets the routing slip carried by <paramref name="mhc"/>. A malformed slip is reported as no slip,
        /// silently. Use the <see cref="ILogger"/> overload to get an operator signal when that happens.
        /// </summary>
        public static bool TryGetRoutingSlip(this IMessageHandlerContext mhc, out RoutingSlip routingSlip)
            => mhc.TryGetRoutingSlip(null, out routingSlip);

        /// <summary>
        /// Gets the routing slip carried by <paramref name="mhc"/>, warning through <paramref name="logger"/>
        /// when the slip is malformed.
        /// </summary>
        public static bool TryGetRoutingSlip(this IMessageHandlerContext mhc, ILogger logger, out RoutingSlip routingSlip)
        {
            if (mhc is IMessageBrokerContext mbc)
            {
                if (mbc.TryGetRoutingSlip(logger, out var rs))
                {
                    routingSlip = rs;
                    return true;
                }
            }
            routingSlip = null;
            return false;
        }

        public static Task Send<TMessage>(this IMessageHandlerContext context,
                            TMessage message,
                            RoutingSlip slip,
                            SendOptions options = null) where TMessage : ICommand
        {
            if (context.TryGetBrokeredMessageDispatcher(out var bmd))
            {
                return bmd.Send(message, slip, context.GetTransactionContext(), options);
            }
            return Task.CompletedTask;
        }

        public static Task Forward(this IMessageHandlerContext context, RoutingSlip slip)
        {
            if (context.TryGetBrokeredMessageDispatcher(out var brokeredMessageDispatcher))
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
