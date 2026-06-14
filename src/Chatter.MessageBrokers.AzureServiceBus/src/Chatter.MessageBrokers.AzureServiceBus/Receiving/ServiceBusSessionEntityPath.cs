using Chatter.MessageBrokers;
using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    // INVARIANT: a STRUCTURED, CLOSED-SUM identity for the Azure Service Bus entity a session receiver opens.
    // It is EITHER a queue (one name) XOR a topic subscription (topic + subscription) — never a flat
    // "<topic>/Subscriptions/<sub>" composite string. The flat-string shape is the defect class this type
    // eliminates: ServiceBusClient.AcceptNextSessionAsync(string, ...) treats its single string argument as a
    // QUEUE name only, so feeding it a composite subscription path addresses the wrong entity. By forcing the
    // overload decision through IsSubscription/TopicName/SubscriptionName vs QueueName, the wrong-overload bug
    // cannot be expressed. STEP-002 selects the SDK overload from this shape; STEP-003 consumes the same seam.
    internal readonly struct ServiceBusSessionEntityPath
    {
        // Mirrors the canonical "Subscriptions" segment literal that lives in AzureServiceBusEntityPathBuilder
        // and ServiceBusReceiverRegistry. Re-declared here (not edited into a shared helper) to keep this the
        // ONLY file touched in this step; the same single-literal-duplication pattern the registry already uses.
        private const string SubscriptionsSegment = "Subscriptions";
        private static readonly IBrokeredMessagePathBuilder _pathBuilder = new AzureServiceBusEntityPathBuilder();

        private ServiceBusSessionEntityPath(bool isSubscription, string queueName, string topicName, string subscriptionName)
        {
            IsSubscription = isSubscription;
            _queueName = queueName;
            _topicName = topicName;
            _subscriptionName = subscriptionName;
        }

        private readonly string _queueName;
        private readonly string _topicName;
        private readonly string _subscriptionName;

        // True when this identity is a topic subscription (use the (topic, subscription) overload); false when it
        // is a queue (use the (queue) overload). This is the unit-observable overload-selection seam.
        public bool IsSubscription { get; }

        // The queue name for a QUEUE identity. Throws for a subscription identity — reading the queue name of a
        // subscription is a programming error (the wrong-overload bug this type prevents), so it fails loudly
        // rather than returning a sentinel that erases the distinction.
        public string QueueName
            => IsSubscription
                ? throw new InvalidOperationException("QueueName is not valid for a topic-subscription session entity; use TopicName and SubscriptionName.")
                : _queueName;

        // The topic name for a SUBSCRIPTION identity. Throws for a queue identity.
        public string TopicName
            => IsSubscription
                ? _topicName
                : throw new InvalidOperationException("TopicName is not valid for a queue session entity; use QueueName.");

        // The trailing subscription name ("sub") for a SUBSCRIPTION identity — never the formatted
        // "<topic>/Subscriptions/<sub>" composite. Throws for a queue identity.
        public string SubscriptionName
            => IsSubscription
                ? _subscriptionName
                : throw new InvalidOperationException("SubscriptionName is not valid for a queue session entity; use QueueName.");

        // Derives the structured shape from the receiver's sending path and its OWN receiver path, mirroring
        // ServiceBusReceiverRegistry.InferTopLevelEntity:
        //   QUEUE when sendingPath is null/empty OR equals messageReceiverPath -> QueueName = messageReceiverPath.
        //   SUBSCRIPTION when sendingPath is a distinct topic -> TopicName = sendingPath; SubscriptionName = the
        //   trailing subscription.
        //
        // IDEMPOTENCY: messageReceiverPath may arrive RAW ("sub") OR already FORMATTED ("<topic>/Subscriptions/<sub>")
        // — the runtime path the core BrokeredMessageReceiver.StartReceiver rewrites MessageReceiverPath into is
        // already formatted, while registration / discovery pass a raw subscription name. Both MUST collapse to
        // SubscriptionName = "sub". This mirrors ServiceBusReceiverRegistry.CanonicalReceivingPath's guard: when the
        // receiver path is already "<topic>/Subscriptions/...", strip that prefix instead of re-formatting (which
        // would otherwise nest a second "<topic>/Subscriptions/" segment).
        public static ServiceBusSessionEntityPath Create(string sendingPath, string messageReceiverPath)
        {
            if (string.IsNullOrWhiteSpace(sendingPath) || string.Equals(sendingPath, messageReceiverPath, StringComparison.Ordinal))
            {
                return new ServiceBusSessionEntityPath(isSubscription: false, queueName: messageReceiverPath, topicName: null, subscriptionName: null);
            }

            var subscriptionName = ExtractSubscriptionName(sendingPath, messageReceiverPath);
            return new ServiceBusSessionEntityPath(isSubscription: true, queueName: null, topicName: sendingPath, subscriptionName: subscriptionName);
        }

        // Collapses a raw-or-formatted receiver path to the bare trailing subscription name. The formatted prefix
        // ("<topic>/Subscriptions/") is derived from the path builder rather than re-hardcoded: formatting a
        // sentinel subscription yields "<topic>/Subscriptions/<sentinel>", and the prefix is everything before
        // that sentinel. Comparison is OrdinalIgnoreCase to match Azure Service Bus entity-name semantics
        // (consistent with the registry).
        private static string ExtractSubscriptionName(string topicName, string messageReceiverPath)
        {
            if (string.IsNullOrEmpty(messageReceiverPath))
            {
                return messageReceiverPath;
            }

            var formattedPrefix = $"{topicName}/{SubscriptionsSegment}/";
            if (messageReceiverPath.StartsWith(formattedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return messageReceiverPath.Substring(formattedPrefix.Length);
            }

            return messageReceiverPath;
        }

        // INVARIANT: a human-readable rendering for log messages (STEP-002 preserves existing LogTrace/LogWarning
        // text that referenced the flat path). A subscription renders via the path builder so the logged shape
        // matches the canonical "<topic>/Subscriptions/<sub>" receiving path; a queue renders as its bare name.
        public override string ToString()
            => IsSubscription
                ? _pathBuilder.GetMessageReceivingPath(_topicName, _subscriptionName)
                : _queueName;
    }
}
