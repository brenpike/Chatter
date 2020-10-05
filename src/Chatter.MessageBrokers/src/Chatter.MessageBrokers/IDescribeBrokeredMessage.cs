using Chatter.MessageBrokers.Receiving;

namespace Chatter.MessageBrokers
{
    public interface IDescribeBrokeredMessage
    {
        /// <summary>
        /// True if the receiver will automatically receive messages upon creation.
        /// </summary>
        public bool AutoReceiveMessages { get; }

        /// <summary>
        /// Describes the receiver. Used to track progress using the 'Via' user property of the <see cref="InboundBrokeredMessage"/>.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the name of the current destination path.
        /// </summary>
        public string DestinationPath { get; }

        /// <summary>
        /// Gets the name of the path to receive messages.
        /// </summary>
        public string MessageReceiverPath { get; }
    }
}
