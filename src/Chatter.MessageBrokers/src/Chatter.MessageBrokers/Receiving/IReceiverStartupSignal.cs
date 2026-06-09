using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// An internal startup-completion seam for brokered message receivers. The concrete
    /// <see cref="BrokeredMessageReceiver{TMessage}"/> produces this signal; the internal
    /// <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> is its only consumer.
    /// </summary>
    internal interface IReceiverStartupSignal
    {
        /// <summary>
        /// A <see cref="Task"/> that completes successfully exactly when the receiver goes live
        /// (the moment <see cref="IReceiveMessages.IsReceiving"/> transitions to <c>true</c>). It provides an
        /// awaitable startup-completion signal so callers can gate on the receiver reaching steady state without
        /// polling <see cref="IReceiveMessages.IsReceiving"/>. It never faults: a startup-fatal failure surfaces
        /// through the receive task itself, not this signal.
        /// </summary>
        Task ReceivingStarted { get; }
    }
}
