using Chatter.MessageBrokers.Receiving;
using Newtonsoft.Json;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class InboundBrokeredMessageExtensions
    {
        public static InboundBrokeredMessage WithRoutingSlip(this InboundBrokeredMessage message, RoutingSlip slip)
        {
            var serializedRoutingSlip = JsonConvert.SerializeObject(slip);
            message.MessageContextImpl[MessageContext.RoutingSlip] = serializedRoutingSlip;
            return message;
        }
    }
}
