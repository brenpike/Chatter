namespace Chatter.MessageBrokers.Receiving
{
    public interface IReceiveMessages
    {
        /// <summary>
        /// Indicates if messages are currently being received
        /// </summary>
        public bool IsReceiving { get; }
    }
}
