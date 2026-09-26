namespace Chatter.MessageBrokers.Reliability.Inbox
{
    /// <summary>
    /// The Delivery Entry: the message a receive attempt admitted into the command pipeline first. It lives in the
    /// delivery's <see cref="Chatter.CQRS.Context.ContextContainer"/>, and the receiver replaces it at the start of
    /// every Recovery attempt, so it is scoped to one attempt of one delivery.
    /// </summary>
    internal sealed class InboxDeliveryEntry
    {
        // INVARIANT: no lock. The entry lives in the delivery's ContextContainer, which is unsynchronized and must
        // never be used from two threads at once (ADR-0011). One worker owning the delivery's container and running
        // its Recovery attempts one after another is a construction fact; a handler awaiting each nested dispatch
        // before starting the next is NOT - ADR-0011 places that on the application as a requirement it deliberately
        // does not enforce, and records that a missing await is enough to break it. An application that breaks it
        // corrupts the container's own Dictionary before _admittedMessage is ever read, so this field adds no
        // exposure the container does not already carry; ADR-0011 holds the rationale and #333 is where reopening it
        // belongs. While that requirement holds, the first Admits call has bound the attempt's entry before any other
        // call on this context runs. No test pins it.
        private object _admittedMessage;

        /// <summary>
        /// Binds the entry to <paramref name="message"/> on the first call; thereafter reports whether
        /// <paramref name="message"/> is that same instance.
        /// </summary>
        public bool Admits(object message)
        {
            _admittedMessage ??= message;
            return ReferenceEquals(_admittedMessage, message);
        }
    }
}
