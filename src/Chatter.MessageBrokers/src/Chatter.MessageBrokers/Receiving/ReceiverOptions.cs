namespace Chatter.MessageBrokers.Receiving
{
    public class ReceiverOptions
    {
        public string Description { get; set; }

        /// <summary>
        /// Gets the name of the path to receive messages.
        /// </summary>
        public string MessageReceiverPath { get; set; }

        /// <summary>
        /// The path that the message was sent on before being received by <see cref="MessageReceiverPath"/>. May not always be required,
        /// however, some messaging infrastructures may leverage the sending path to build the <see cref="MessageReceiverPath"/> using
        /// <see cref="IBrokeredMessagePathBuilder"/>.
        /// </summary>
        public string SendingPath { get; set; }

        /// <summary>
        /// Gets the name of the path to send messages on error. Used by <see cref="IRecoveryAction"/>
        /// </summary>
        public string ErrorQueuePath { get; set; }

        /// <summary>
        /// The path of the deadletter queue. Messages will be forwarded to this queue if they are unable to be received after max configured retries, failed IRecoveryAction or if they are poisoned.
        /// This property may be ignored by message broker infrastructure which have their own DLQ implementation.
        /// </summary>
        public string DeadLetterQueuePath { get; set; }

        /// <summary>
        /// The type of transactionality the message will be part of while being received by the messaging infrastructure
        /// </summary>
        public TransactionMode? TransactionMode { get; set; }

        /// <summary>
        /// The type of messaging infrastructure to use. If no infrastructure type is provided, the first infrastructure type configured
        /// during startup will be used.
        /// </summary>
        public string InfrastructureType { get; set; } = "";

        /// <summary>
        /// The max number of attempts that will be made to receive a message from a queue/subscription before the message is deadlettered.
        /// Messaging infrastructure implementation will take precedence. If this value is set to 11 but the messaging infrastructure's
        /// "max delivery count" is set to 10, the message will only be attemped to be received 10 times and thus any application logic triggered by
        /// comparing actual receive attempts to the max will not execute.
        /// </summary>
        public int MaxReceiveAttempts { get; set; } = 10;
    }
}
