﻿namespace Chatter.MessageBrokers.Receiving
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
        /// Gets the name of the path to send messages on error.
        /// </summary>
        public string ErrorQueuePath { get; set; }

        /// <summary>
        /// The type of transactionality the message will be part of while being received by the messaging infrastructure
        /// </summary>
        public TransactionMode? TransactionMode { get; set; }

        /// <summary>
        /// The type of messaging infrastructure to use. If no infrastructure type is provided, the first infrastructure type configured
        /// during startup will be used.
        /// </summary>
        public string InfrastructureType { get; set; } = "";
    }
}
