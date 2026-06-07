using Chatter.MessageBrokers.AzureServiceBus.Extensions;
using Chatter.MessageBrokers.Context;
using Microsoft.Azure.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Pure, I/O-free shaping of an Azure Service Bus <see cref="Message"/> into a
    /// <see cref="MessageBrokerContext"/>. Extracted VERBATIM from
    /// <see cref="ServiceBusReceiver.ReceiveMessageAsync"/>: body-converter selection (with try/catch
    /// fallback to <see cref="JsonBodyConverter"/>), the four header stamps, and context assembly.
    /// The transaction-context includes that touch the live receiver/connection stay in
    /// <see cref="ServiceBusReceiver"/> because they are not I/O-free.
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
        public MessageBrokerContext CreateContext(Message message, string messageReceiverPath, CancellationToken cancellationToken)
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
                message.AddUserProperty(MessageContext.TimeToLive, message.TimeToLive);
                message.AddUserProperty(MessageContext.ExpiryTimeUtc, message.ExpiresAtUtc);
                message.AddUserProperty(MessageContext.InfrastructureType, ASBMessageContext.InfrastructureType);
                message.AddUserProperty(MessageContext.ReceiveAttempts, message.SystemProperties.DeliveryCount);

                messageContext = new MessageBrokerContext(message.MessageId, message.Body, message.UserProperties, messageReceiverPath, cancellationToken, bodyConverter);

                messageContext.Container.Include(message);
            }

            return messageContext;
        }
    }
}
