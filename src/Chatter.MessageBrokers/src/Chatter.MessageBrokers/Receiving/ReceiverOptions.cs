namespace Chatter.MessageBrokers.Receiving
{
    public class ReceiverOptions
    {
        public string Description { get; set; }

        /// <summary>
        /// Gets the name of the path to receive messages.
        /// </summary>
        public string MessageReceiverPath { get; set; }

        public string SendingPath { get; set; }

        /// <summary>
        /// Gets the name of the path to send messages on error.
        /// </summary>
        public string ErrorQueuePath { get; set; }

        public TransactionMode? TransactionMode { get; set; }

        public string InfrastructureType { get; set; } = "";
    }
}
