using Chatter.MessageBrokers.Context;
using System.Text.Json;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class MessageBrokerContextExtensions
    {
        public static bool TryGetRoutingSlip(this IMessageBrokerContext mbc, out RoutingSlip routingSlip)
        {
            try
            {
                if (mbc.BrokeredMessage != null)
                {
                    if (mbc.BrokeredMessage.MessageContext != null)
                    {
                        if (mbc.BrokeredMessage.MessageContext.TryGetValue(MessageContext.RoutingSlip, out var rs))
                        {
                            RoutingSlip theSlip = JsonSerializer.Deserialize<RoutingSlip>((string)rs, ChatterJson.Options);
                            routingSlip = theSlip;
                            return true;
                        }
                    }
                }

                if (mbc.Container.TryGet<RoutingSlip>(out var slipFromContainer))
                {
                    routingSlip = slipFromContainer;
                    return true;
                }

                routingSlip = null;
                return false;
            }
            catch
            {
                routingSlip = null;
                return false;
            }
        }
    }
}
