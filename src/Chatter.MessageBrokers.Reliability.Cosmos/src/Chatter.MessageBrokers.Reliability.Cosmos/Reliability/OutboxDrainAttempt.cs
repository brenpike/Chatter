namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The PHASE one Outbox Relay drain attempt reached: whether that attempt got PAST its publish, so a failure after
    /// the message went out can be told apart from a failure before it. It carries exactly one fact —
    /// <see cref="MessagePublished"/> — and that fact is derived from the relay's OWN control flow (the publish
    /// returned), NEVER from the type of any exception: classifying a provider exception is intractable, but the relay
    /// is the thing that publishes, so knowing whether it published is not.
    /// </summary>
    /// <remarks>
    /// The CALLER owns the instance, never the relay. That is load-bearing twice over: the phase must survive the relay
    /// THROWING out of its drain, and it must survive the disposal of the per-document scope the drain ran under — a
    /// scope-disposal failure after a publish that returned is still a POST-publish failure.
    /// </remarks>
    internal sealed class OutboxDrainAttempt
    {
        /// <summary>Whether this attempt's publish RETURNED, i.e. whether the brokered message reached the broker.</summary>
        internal bool MessagePublished { get; private set; }

        /// <summary>Records that this attempt's publish returned. Called ONLY immediately after the publish returns.</summary>
        internal void MarkPublished() => MessagePublished = true;
    }
}
