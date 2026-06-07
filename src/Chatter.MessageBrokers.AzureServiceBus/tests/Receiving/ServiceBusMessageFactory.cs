using Microsoft.Azure.ServiceBus;
using System;
using System.Reflection;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving
{
    // Builds Microsoft.Azure.ServiceBus.Message instances that behave as if RECEIVED from the broker.
    // The legacy SDK (v5.2.0) only populates SystemProperties on receive and guards DeliveryCount /
    // LockToken behind ThrowIfNotReceived (driven by the internal sequenceNumber field), so a unit
    // test must prime those internal members via reflection to exercise the inbound shaping path.
    internal static class ServiceBusMessageFactory
    {
        public static Message ReceivedMessage(byte[] body = null,
                                              string messageId = "message-id",
                                              string contentType = "application/json",
                                              int deliveryCount = 1,
                                              Guid? lockToken = null)
        {
            var message = new Message(body ?? new byte[] { 1 })
            {
                MessageId = messageId,
                ContentType = contentType,
            };

            var sp = message.SystemProperties;
            var spType = typeof(Message.SystemPropertiesCollection);

            // sequenceNumber > 0 satisfies ThrowIfNotReceived so DeliveryCount/LockToken are readable.
            spType.GetField("sequenceNumber", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(sp, 1L);
            spType.GetProperty("DeliveryCount").GetSetMethod(true).Invoke(sp, new object[] { deliveryCount });
            spType.GetField("lockTokenGuid", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(sp, lockToken ?? Guid.NewGuid());

            return message;
        }
    }
}
