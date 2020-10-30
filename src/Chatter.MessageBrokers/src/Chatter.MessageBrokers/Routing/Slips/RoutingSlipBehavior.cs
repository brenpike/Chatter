using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlipBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : IMessage
    {
        private readonly ILogger<RoutingSlipBehavior<TMessage>> _logger;

        public RoutingSlipBehavior(ILogger<RoutingSlipBehavior<TMessage>> logger)
            => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            if (!(messageHandlerContext is IMessageBrokerContext messageBrokerContext))
            {
                await next().ConfigureAwait(false);
                return;
            }

            if (!(messageBrokerContext.BrokeredMessage.MessageContext.TryGetValue(MessageContext.RoutingSlip, out var routingSlip)))
            {
                _logger.LogTrace($"No routing slip found. Continuing pipeline execution.");
                await next().ConfigureAwait(false);
                return;
            }

            RoutingSlip theSlip = JsonConvert.DeserializeObject<RoutingSlip>((string)routingSlip);

            messageBrokerContext.Container.Include(theSlip);

            await next().ConfigureAwait(false);

            try
            {
                await messageBrokerContext.Forward(theSlip).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogTrace($"Error forwarding message '{typeof(TMessage).Name}': {e.StackTrace}");
                throw;
            }
        }
    }
}
