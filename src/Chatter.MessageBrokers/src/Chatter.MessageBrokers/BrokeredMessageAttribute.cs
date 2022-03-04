using System;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Decorate <see cref="CQRS.IMessage"/> classes with metadata to be used by a message broker to route messages.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class BrokeredMessageAttribute : Attribute
    {
        public string SendingPath { get; }
        public string ReceiverName { get; }
        public string ErrorQueueName { get; }
        public string MessageDescription { get; }
        public string InfrastructureType { get; }
        public string DeadletterQueueName { get; }

        /// <summary>
        /// Creates a brokered message that can send and receive on different exchanges/entity paths (i.e., queues/topics).
        /// Chatter will automatically begin receiving messages for <paramref name="receivingPath"/> if a non-null o non-whitespace value is supplied.
        /// </summary>
        /// <param name="sendingPath">The name of the message broker message to send.</param>
        /// <param name="receivingPath">The name of the message broker message to receive. Can be a queue or subscription, etc.</param>
        /// <param name="errorQueueName"></param>
        /// <param name="messageDescription"></param>
        /// <param name="infrastructureType"></param>
        public BrokeredMessageAttribute(string sendingPath,
                                        string receivingPath = null,
                                        string errorQueueName = null,
                                        string messageDescription = null,
                                        string infrastructureType = "",
                                        string deadletterQueueName = null)
        {
            if (string.IsNullOrWhiteSpace(sendingPath) && string.IsNullOrWhiteSpace(receivingPath))
            {
                throw new ArgumentException($"A '{nameof(sendingPath)}' or '{nameof(receivingPath)}' is required.");
            }

            SendingPath = sendingPath;
            ReceiverName = receivingPath;
            ErrorQueueName = errorQueueName;
            MessageDescription = messageDescription;
            InfrastructureType = infrastructureType;
            DeadletterQueueName = deadletterQueueName;
        }
    }
}
