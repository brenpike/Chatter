using Chatter.MessageBrokers.Routing.Options;
using Newtonsoft.Json;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class SendOptionsExtensions
    {
        public static SendOptions WithRoutingSlip(this SendOptions options, RoutingSlip slip)
        {
            var serializedRoutingSlip = JsonConvert.SerializeObject(slip);
            options.SetApplicationProperty(MessageContext.RoutingSlip, serializedRoutingSlip);
            return options;
        }
    }
}
