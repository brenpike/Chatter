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
        // never be used from two threads at once (ADR-0011): one worker owns the delivery's container, runs its
        // Recovery attempts one after another, and awaits an attempt's nested dispatches inside its handler, so the
        // first Admits call has bound the attempt's entry before any other call on this context runs. No test pins it.
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
