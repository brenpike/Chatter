using Azure.Messaging.ServiceBus;
using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving
{
    // Builds Azure.Messaging.ServiceBus.ServiceBusReceivedMessage instances that behave as if RECEIVED
    // from the broker. The SDK exposes ServiceBusModelFactory.ServiceBusReceivedMessage for exactly this
    // purpose, so received-state fields (lock token, delivery count, expiry, TTL, application properties)
    // are set via the model factory's named parameters — no reflection.
    internal static class ServiceBusMessageFactory
    {
        public static ServiceBusReceivedMessage ReceivedMessage(byte[] body = null,
                                                                string messageId = "message-id",
                                                                string contentType = "application/json",
                                                                int deliveryCount = 1,
                                                                Guid? lockToken = null,
                                                                TimeSpan? timeToLive = null,
                                                                DateTimeOffset? enqueuedTime = null)
        {
            var ttl = timeToLive ?? TimeSpan.FromMinutes(5);
            var enqueued = enqueuedTime ?? DateTimeOffset.UtcNow;

            return ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: new BinaryData(body ?? new byte[] { 1 }),
                messageId: messageId,
                contentType: contentType,
                timeToLive: ttl,
                deliveryCount: deliveryCount,
                lockTokenGuid: lockToken ?? Guid.NewGuid(),
                enqueuedTime: enqueued);
        }
    }
}
