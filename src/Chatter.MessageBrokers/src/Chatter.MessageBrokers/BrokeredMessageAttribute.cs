using System;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Decorate <see cref="CQRS.IMessage"/> classes with metadata to be used by a message broker to route messages.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class BrokeredMessageAttribute : Attribute
    {
        public string MessageName { get; }
        public string ReceiverName { get; }
        public string ErrorQueueName { get; }
        public string MessageDescription { get; }

        /// <summary>
        /// Creates a brokered message that can send and receive on different exchanges/entity paths (i.e., queues/topics).
        /// Chatter will automatically begin receiving messages for <paramref name="receiverName"/> if a non-null or whitespace value is supplied.
        /// </summary>
        /// <param name="messageName">The name of the message broker message to send.</param>
        /// <param name="receiverName">he name of the message broker message to receive. Can be a queue or subscription, etc.</param>
        public BrokeredMessageAttribute(string messageName, string receiverName = null, string errorQueueName = null)
        {
            if (string.IsNullOrWhiteSpace(messageName) && string.IsNullOrWhiteSpace(receiverName))
            {
                throw new ArgumentException($"A '{nameof(messageName)}' or '{nameof(receiverName)}' is required.");
            }

            MessageName = messageName;
            ReceiverName = receiverName;
            ErrorQueueName = errorQueueName;
        }
    }
}
