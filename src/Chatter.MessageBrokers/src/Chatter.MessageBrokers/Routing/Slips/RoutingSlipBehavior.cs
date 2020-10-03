using Chatter.CQRS;
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
    public class RoutingSlipBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : IMessage
    {
        private readonly ILogger<RoutingSlipBehavior<TMessage>> _logger;

        public RoutingSlipBehavior(ILogger<RoutingSlipBehavior<TMessage>> logger) 
            => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            if (messageHandlerContext is IMessageBrokerContext messageBrokerContext)
            {
                if (!(messageBrokerContext.BrokeredMessage.MessageContext.TryGetValue(MessageContext.RoutingSlip, out var routingSlip)))
                {
                    _logger.LogTrace($"No routing slip found. Continuing pipeline execution.");
                    await next().ConfigureAwait(false);
                    return;
                }

                RoutingSlip theSlip = JsonConvert.DeserializeObject<RoutingSlip>((string)routingSlip);

                messageBrokerContext.Container.Include(theSlip);

                try
                {
                    await next().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    //TODO: we don't want to compensate right away do we? don't we want to retry a few times to rule out transient errors?
                    theSlip.Compensate();
                    _logger.LogTrace($"Error receiving message '{typeof(TMessage).Name}'. Compensating routing slip: {e.StackTrace}");
                    throw;
                }
                finally
                {
                    try
                    {
                        var destination = theSlip.Route.FirstOrDefault()?.DestinationPath;
                        if (!string.IsNullOrWhiteSpace(destination))
                        {
                            //TODO:get rid of casting
                            await messageBrokerContext.Send((ICommand)message, theSlip).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogTrace($"No destination specified");
                        }
                    }
                    catch (Exception)
                    {
                        //TODO: exception handling...custom ROutingSlipException for when routing tothe next step fails? logging? what do we do here?
                        throw;
                    }
                }
            }
            else
            {
                await next().ConfigureAwait(false);
            }
        }
    }
}
