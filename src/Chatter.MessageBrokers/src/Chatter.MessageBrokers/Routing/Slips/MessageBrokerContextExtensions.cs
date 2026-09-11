using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using System;
using System.Text.Json;

namespace Chatter.MessageBrokers.Routing.Slips
{
    public static class MessageBrokerContextExtensions
    {
        /// <summary>
        /// Gets the routing slip carried by <paramref name="mbc"/>. A malformed slip is reported as no slip,
        /// silently. Use the <see cref="ILogger"/> overload to get an operator signal when that happens.
        /// </summary>
        public static bool TryGetRoutingSlip(this IMessageBrokerContext mbc, out RoutingSlip routingSlip)
            => mbc.TryGetRoutingSlip(null, out routingSlip);

        /// <summary>
        /// Gets the routing slip carried by <paramref name="mbc"/>, warning through <paramref name="logger"/>
        /// when the slip is malformed.
        /// </summary>
        /// <remarks>
        /// INVARIANT: only a MALFORMED slip value returns false — one that is not a string, is a null or
        /// whitespace string, is unparsable JSON, or parses to a JSON null. Every other exception propagates,
        /// so a genuine fault is never mistaken for "no slip present".
        /// </remarks>
        public static bool TryGetRoutingSlip(this IMessageBrokerContext mbc, ILogger logger, out RoutingSlip routingSlip)
        {
            var brokeredMessage = mbc.BrokeredMessage;
            if (brokeredMessage?.MessageContext != null
                && brokeredMessage.MessageContext.TryGetValue(MessageContext.RoutingSlip, out var rs))
            {
                return TryDeserializeRoutingSlip(rs, brokeredMessage.MessageId, logger, out routingSlip);
            }

            if (mbc.Container.TryGet<RoutingSlip>(out var slipFromContainer))
            {
                routingSlip = slipFromContainer;
                return true;
            }

            routingSlip = null;
            return false;
        }

        private static bool TryDeserializeRoutingSlip(object slipValue, string messageId, ILogger logger, out RoutingSlip routingSlip)
        {
            routingSlip = null;

            // INVARIANT: the slip value is TYPE-TESTED, never cast. A non-string value is a malformed slip,
            // not a fault, so it must not surface as an InvalidCastException.
            if (!(slipValue is string serializedSlip) || string.IsNullOrWhiteSpace(serializedSlip))
            {
                LogMalformedRoutingSlip(logger, messageId);
                return false;
            }

            RoutingSlip deserializedSlip;
            try
            {
                // Attachments (IDictionary<string, object>) values are materialized to the CLR
                // types Newtonsoft's untyped read produced during this deserialize by the global
                // MaterializingObjectConverter on ChatterJson.Options, so callers that set
                // slip.Attachments["foo"] = "bar" and read it back as string/int after
                // TryGetRoutingSlip don't hit cast failures — no per-seam materialization needed.
                deserializedSlip = JsonSerializer.Deserialize<RoutingSlip>(serializedSlip, ChatterJson.Options);
            }
            catch (JsonException e)
            {
                LogMalformedRoutingSlip(logger, messageId, e);
                return false;
            }

            if (deserializedSlip is null)
            {
                LogMalformedRoutingSlip(logger, messageId);
                return false;
            }

            routingSlip = deserializedSlip;
            return true;
        }

        private static void LogMalformedRoutingSlip(ILogger logger, string messageId, Exception cause = null)
            => logger?.LogWarning(cause, $"A malformed routing slip was found on message '{messageId}'. The message will not be routed to its next destination.");
    }
}
