using Chatter.MessageBrokers.Receiving;
using System.Text.Json;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class InboundBrokeredMessageExtensions
    {
        public static InboundBrokeredMessage WithRoutingSlip(this InboundBrokeredMessage message, RoutingSlip slip)
        {
            var serializedRoutingSlip = JsonSerializer.Serialize(slip, ChatterJson.Options);
            message.MessageContextImpl[MessageContext.RoutingSlip] = serializedRoutingSlip;
            return message;
        }
    }
}
