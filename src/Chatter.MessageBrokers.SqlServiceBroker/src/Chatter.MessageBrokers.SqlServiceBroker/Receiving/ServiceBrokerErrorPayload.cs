using System;
using System.Text;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    internal static class ServiceBrokerErrorPayload
    {
        internal const string NoErrorPayloadSentinel = "<no error payload>";

        // INVARIANT: Service Broker system Error message bodies are the documented
        // <Error><Code/><Description/></Error> XML, encoded UTF-16 (Encoding.Unicode) and prefixed with a
        // UTF-16LE byte order mark (the bytes FF FE 3C 00, observed live). Encoding.Unicode.GetString keeps that
        // mark as a leading U+FEFF and string.Trim() does not remove it on .NET Core, so it is stripped
        // explicitly; deleting the TrimStart reddens
        // WhenDescribingAnErrorPayload.MustNotIncludeAByteOrderMark (ADR-0027). No test pins the encoding
        // choice itself, but ReceiveMessageFromQueueCommand's gzip test (SUBSTRING(message_body, 1, 2)
        // = 0x1F8B) is what makes UTF-16 safe to decode here: a UTF-16LE byte order mark is the byte pair
        // 0xFF 0xFE, not the 0x1F 0x8B gzip magic, so an Error body always passes the receive query through
        // undecompressed.
        public static string Describe(byte[] body)
        {
            if (body is null || body.Length == 0)
            {
                return NoErrorPayloadSentinel;
            }

            return Encoding.Unicode.GetString(body).TrimStart('﻿').Trim();
        }
    }
}
