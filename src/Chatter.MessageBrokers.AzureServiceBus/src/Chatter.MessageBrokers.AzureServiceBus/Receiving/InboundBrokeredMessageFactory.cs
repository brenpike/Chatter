using Chatter.MessageBrokers.Context;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Pure, I/O-free shaping of an Azure Service Bus <see cref="ServiceBusReceivedMessage"/> into a
    /// <see cref="MessageBrokerContext"/>: body-converter selection (with try/catch fallback to
    /// <see cref="JsonBodyConverter"/>), the four header stamps, and context assembly. The
    /// transaction-context includes that touch the live receiver stay in <see cref="ServiceBusReceiver"/>
    /// because they are not I/O-free.
    /// </summary>
    internal class InboundBrokeredMessageFactory
    {
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly ILogger _logger;

        public InboundBrokeredMessageFactory(IBodyConverterFactory bodyConverterFactory, ILogger logger)
        {
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Shapes <paramref name="message"/> into a <see cref="MessageBrokerContext"/>, stamping the
        /// infrastructure headers and selecting a body converter (defaulting to
        /// <see cref="JsonBodyConverter"/> when none can be created for the content type). Returns
        /// null when <paramref name="message"/> is null. The received message is included in the
        /// returned context's container.
        /// </summary>
        /// <remarks>
        /// INVARIANT: <see cref="ServiceBusReceivedMessage.ApplicationProperties"/> is read-only, so the
        /// four header stamps (TTL, expiry, infrastructure type, receive attempts) are written into the
        /// MUTABLE header dictionary handed to the context — never back onto the received message — and
        /// the values are sourced from the message's top-level properties.
        /// </remarks>
        public MessageBrokerContext CreateContext(ServiceBusReceivedMessage message, string messageReceiverPath, CancellationToken cancellationToken)
        {
            if (message is null)
            {
                return null;
            }

            MessageBrokerContext messageContext;
            IBrokeredMessageBodyConverter bodyConverter = new JsonBodyConverter();

            try
            {
                bodyConverter = _bodyConverterFactory.CreateBodyConverter(message.ContentType);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"Error creating body converter for content type '{message.ContentType}'. Defaulting to {nameof(JsonBodyConverter)}.");
            }
            finally
            {
                var headers = new Dictionary<string, object>();
                foreach (var property in message.ApplicationProperties)
                {
                    headers[property.Key] = property.Value;
                }

                headers[MessageContext.TimeToLive] = message.TimeToLive;
                headers[MessageContext.ExpiryTimeUtc] = message.ExpiresAt.UtcDateTime;
                headers[MessageContext.InfrastructureType] = ASBMessageContext.InfrastructureType;
                headers[MessageContext.ReceiveAttempts] = message.DeliveryCount;

                messageContext = new MessageBrokerContext(message.MessageId, message.Body.ToArray(), headers, messageReceiverPath, cancellationToken, bodyConverter);

                messageContext.Container.Include(message);
            }

            return messageContext;
        }
    }
}
