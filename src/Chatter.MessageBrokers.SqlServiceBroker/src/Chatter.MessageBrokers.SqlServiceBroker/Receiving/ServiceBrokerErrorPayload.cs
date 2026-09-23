using System;
using System.Text;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    internal static class ServiceBrokerErrorPayload
    {
        internal const string NoErrorPayloadSentinel = "<no error payload>";

        // INVARIANT: Service Broker system Error message bodies are the documented
        // <Error><Code/><Description/></Error> XML, encoded UTF-16 (Encoding.Unicode). No test pins the
        // encoding choice itself, but ReceiveMessageFromQueueCommand's gzip test (SUBSTRING(message_body, 1, 2)
        // = 0x1F8B) is what makes UTF-16 safe to decode here: a UTF-16LE '<' is the byte pair 0x3C 0x00, not the
        // 0x1F 0x8B gzip magic, so an Error body always passes the receive query through undecompressed.
        public static string Describe(byte[] body)
        {
            if (body is null || body.Length == 0)
            {
                return NoErrorPayloadSentinel;
            }

            return Encoding.Unicode.GetString(body).Trim();
        }
    }
}
