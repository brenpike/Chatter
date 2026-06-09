using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    public interface IReceiveMessages
    {
        /// <summary>
        /// Indicates if messages are currently being received
        /// </summary>
        public bool IsReceiving { get; }

        /// <summary>
        /// A <see cref="Task"/> that completes successfully exactly when the receiver goes live
        /// (the moment <see cref="IsReceiving"/> transitions to <c>true</c>). It provides an awaitable
        /// startup-completion signal so callers can gate on the receiver reaching steady state without
        /// polling <see cref="IsReceiving"/>. It never faults: a startup-fatal failure surfaces through the
        /// receive task itself, not this signal.
        /// </summary>
        public Task ReceivingStarted { get; }
    }
}
