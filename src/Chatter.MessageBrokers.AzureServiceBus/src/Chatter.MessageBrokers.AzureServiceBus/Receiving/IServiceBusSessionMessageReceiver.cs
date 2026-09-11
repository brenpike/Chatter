using Azure.Messaging.ServiceBus;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Internal port over the session-mode operations the <see cref="ServiceBusReceiver"/> needs beyond the
    /// settlement contract of <see cref="IServiceBusMessageReceiver"/>. It is deliberately phrased PER MESSAGE
    /// rather than per receiver, so one implementation may hold exactly ONE session
    /// (<see cref="AzureSdkSessionMessageReceiverAdapter"/>) and another may hold N sessions concurrently and
    /// answer for each of them.
    /// </summary>
    internal interface IServiceBusSessionMessageReceiver : IServiceBusMessageReceiver
    {
        /// <summary>
        /// Answers which held session receiver delivered <paramref name="message"/>, or null when no held session
        /// delivered it. The <see cref="ServiceBusReceiver"/> puts the answer in the per-message transaction
        /// <c>Container</c> so session settlement and the session-state extension resolve the RIGHT session
        /// receiver for the message being handled.
        /// </summary>
        /// <param name="message">The message whose delivering session receiver is being resolved.</param>
        ServiceBusSessionReceiver SessionReceiverFor(ServiceBusReceivedMessage message);

        /// <summary>
        /// Signals that the worker is finished with <paramref name="message"/>'s delivery, whether or not it was
        /// settled. An implementation holding exactly one session has no slot to free and treats this as a no-op;
        /// an implementation holding N sessions frees the session slot the delivery occupied.
        /// </summary>
        /// <param name="message">The delivery the worker has finished with.</param>
        void DeliveryReleased(ServiceBusReceivedMessage message);
    }
}
