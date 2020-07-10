namespace Chatter.MessageBrokers.Options
{
    /// <summary>
    /// The mode of transaction
    /// </summary>
    public enum TransactionMode : byte
    {
        /// <summary>
        /// No transaction will be used. If an error occurs after a message is received, it will be lost.
        /// </summary>
        None = 0,
        /// <summary>
        /// Only the receive operation will be part of the transaction
        /// </summary>
        ReceiveOnly = 1,
        /// <summary>
        /// The receiver and all operations that occur during the message receiving process are considered an atomic operation
        /// </summary>
        FullAtomicity = 2,
    }
}
