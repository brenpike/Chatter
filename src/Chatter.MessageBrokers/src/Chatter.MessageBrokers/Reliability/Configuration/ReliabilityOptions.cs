using System.Collections.Generic;
using System.Threading;

namespace Chatter.MessageBrokers.Reliability.Configuration
{
    public class ReliabilityOptions
    {
        public bool RouteMessagesToOutbox { get; internal set; }
        public double MinutesToLiveInMemory { get; internal set; }
        public bool EnableOutboxPollingProcessor { get; internal set; }
        public int OutboxProcessingIntervalInMilliseconds { get; internal set; }

        // BrokeredMessageOutboxProcessor awaits Task.Delay(OutboxProcessingIntervalInMilliseconds, token) OUTSIDE the
        // try/catch that guards a poll pass, so a value Task.Delay rejects faults the whole background service rather
        // than losing one pass. Measured against both target runtimes: Task.Delay(int) throws
        // ArgumentOutOfRangeException below -1, accepts 0, and treats -1 as Timeout.Infinite.
        private const int MinimumOutboxProcessingIntervalInMilliseconds = 0;

        // INVARIANT: every check runs and a single failure names every offending value. An operator who corrected one
        // option, redeployed, and only then discovered the next would pay a deployment per invalid value.
        internal void Validate()
        {
            var violations = new List<string>();
            AddViolationWhenOutboxProcessingIntervalIsBelowMinimum(violations);

            if (violations.Count > 0)
            {
                throw new ReliabilityOptionsValidationException(violations);
            }
        }

        // INVARIANT: -1 is rejected even though Task.Delay accepts it as Timeout.Infinite. An enabled processor that
        // waits forever after its first pass is a disable by inference, and disabling the processor is only
        // expressible through EnableOutboxPollingProcessor - the same opt-in shape the retry policy already uses.
        private void AddViolationWhenOutboxProcessingIntervalIsBelowMinimum(ICollection<string> violations)
        {
            if (OutboxProcessingIntervalInMilliseconds >= MinimumOutboxProcessingIntervalInMilliseconds)
            {
                return;
            }

            var reason = OutboxProcessingIntervalInMilliseconds == Timeout.Infinite
                ? $"which is Timeout.Infinite, so an enabled outbox polling processor would wait forever after its first pass instead of polling. Disable the processor by leaving '{nameof(EnableOutboxPollingProcessor)}' off rather than by an interval that disables it by inference"
                : "which no delay accepts, so the outbox polling processor faults instead of polling";

            violations.Add($"'{nameof(OutboxProcessingIntervalInMilliseconds)}' is {OutboxProcessingIntervalInMilliseconds}, {reason}");
        }
    }
}
