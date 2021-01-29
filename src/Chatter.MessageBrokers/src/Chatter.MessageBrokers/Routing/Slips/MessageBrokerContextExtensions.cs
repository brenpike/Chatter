using Chatter.MessageBrokers.Context;
using Newtonsoft.Json;

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
                            RoutingSlip theSlip = JsonConvert.DeserializeObject<RoutingSlip>((string)rs);
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
