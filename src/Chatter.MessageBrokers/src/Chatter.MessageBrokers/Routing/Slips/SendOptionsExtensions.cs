using Chatter.MessageBrokers.Routing.Options;
using System.Text.Json;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class SendOptionsExtensions
    {
        public static SendOptions WithRoutingSlip(this SendOptions options, RoutingSlip slip)
        {
            var serializedRoutingSlip = JsonSerializer.Serialize(slip, ChatterJson.Options);
            options.WithMessageContext(MessageContext.RoutingSlip, serializedRoutingSlip);
            return options;
        }
    }
}
