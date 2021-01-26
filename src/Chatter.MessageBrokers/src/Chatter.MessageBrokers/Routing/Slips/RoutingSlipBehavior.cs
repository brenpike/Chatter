using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public class RoutingSlipBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
    {
        private readonly ILogger<RoutingSlipBehavior<TMessage>> _logger;

        public RoutingSlipBehavior(ILogger<RoutingSlipBehavior<TMessage>> logger)
            => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            _logger.LogDebug($"Entering {nameof(RoutingSlipBehavior<TMessage>)}.");
            if (!(messageHandlerContext is IMessageBrokerContext messageBrokerContext))
            {
                _logger.LogTrace($"No brokered message context found. Continuing pipeline execution.");
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

            _logger.LogDebug("Continuing pipeline execution.");
            await next().ConfigureAwait(false);

            try
            {
                _logger.LogTrace($"Sending message to '{theSlip.Route?.FirstOrDefault().DestinationPath}'");
                await messageHandlerContext.Send(message, theSlip).ConfigureAwait(false);
                _logger.LogDebug("Sent message to next routing slip destination");

            }
            catch (Exception e)
            {
                _logger.LogTrace(e, $"Error routing message '{typeof(TMessage).Name}' to next routing slip destination ({theSlip.Route?.FirstOrDefault().DestinationPath})");
                throw;
            }
            finally
            {
                _logger.LogDebug($"Finishing {nameof(RoutingSlipBehavior<TMessage>)}.");
            }
        }
    }
}
