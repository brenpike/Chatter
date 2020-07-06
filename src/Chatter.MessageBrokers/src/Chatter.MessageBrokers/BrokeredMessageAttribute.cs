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
        public string NextMessage { get; }
        public string CompensatingMessage { get; }
        public string MessageDescription { get; }
        public bool AutoReceiveMessages { get; }

        /// <summary>
        /// Creates a brokered message that can send and receive on different exchanges/entity paths (i.e., queues/topics).
        /// Chatter will automatically begin receving messages for <paramref name="receiverName"/> if a non-null or whitespace value is supplied.
        /// </summary>
        /// <param name="messageName">The name of the message broker message to send.</param>
        /// <param name="receiverName">he name of the message broker message to receive. Can be a queue or subscription, etc.</param>
        public BrokeredMessageAttribute(string messageName, string receiverName = null)
        {
            if (string.IsNullOrWhiteSpace(messageName))
            {
                throw new ArgumentException("A value of null or white space is not valid.", nameof(messageName));
            }

            MessageName = messageName;
            ReceiverName = receiverName;
            NextMessage = null;
            CompensatingMessage = null;
            AutoReceiveMessages = !string.IsNullOrWhiteSpace(receiverName);
        }

        /// <summary>
        /// Creates a brokered message that sends and receives on the same exchange/entity path (i.e., a queue).
        /// Chatter will automatically begin receving messages for <paramref name="messageName"/>.
        /// </summary>
        /// <param name="messageName">The name of the message broker message to send and receive.</param>
        /// <param name="nextMessage">The name of the message broker destination to forward to after <paramref name="messageName"/> is successfully received.</param>
        /// <param name="compensatingMessage">The name of the message broker destination that will compensate <paramref name="messageName"/> when it is unsuccessfully received.</param>
        /// <param name="messageDescription">A description of the brokered message attribute.</param>
        public BrokeredMessageAttribute(string messageName, string nextMessage, string compensatingMessage = null, string messageDescription = null)
        {
            if (string.IsNullOrWhiteSpace(messageName))
            {
                throw new ArgumentException("A value of null or white space is not valid.", nameof(messageName));
            }

            MessageName = messageName;
            ReceiverName = messageName;
            NextMessage = nextMessage;
            CompensatingMessage = compensatingMessage;
            AutoReceiveMessages = true;
            MessageDescription = string.IsNullOrWhiteSpace(messageDescription) ? messageName : messageDescription;
        }
    }
}
