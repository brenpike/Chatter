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
                            // STJ deserializes Attachments (IDictionary<string, object>) with primitive values
                            // left as raw JsonElement. Materialize them to the CLR types Newtonsoft's untyped
                            // read produced, so consumers that set slip.Attachments["foo"] = "bar" and read it
                            // back as string/int after TryGetRoutingSlip don't hit cast failures. Same seam
                            // treatment applied to the persisted MessageContext; deserialize-side only (wire
                            // output unchanged).
                            theSlip.Attachments = MessageContext.MaterializePersistedContext(theSlip.Attachments);
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
