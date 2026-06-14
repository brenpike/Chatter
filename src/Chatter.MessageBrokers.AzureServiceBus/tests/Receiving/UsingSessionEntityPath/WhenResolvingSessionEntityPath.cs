using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingSessionEntityPath
{
    // Pins the structured overload-selection seam of ServiceBusSessionEntityPath WITHOUT a live Azure Service Bus
    // namespace. The defect class this type eliminates is feeding a flat "<topic>/Subscriptions/<sub>" composite
    // to the queue-only AcceptNextSessionAsync(string, ...) overload; these tests prove the closed-sum resolution
    // (queue XOR subscription), the raw-vs-formatted idempotency at the boundary, and that wrong-arm access fails
    // loudly rather than returning a sentinel.
    public class WhenResolvingSessionEntityPath : Testing.Core.Context
    {
        // (a) Topic subscription resolved from the RUNTIME FORMATTED receiver path — the exact iter-3 defect case.
        // The core receiver rewrites MessageReceiverPath to "<topic>/Subscriptions/<sub>" before the adapter sees
        // it; the structured identity must strip the formatted prefix down to the bare subscription name.
        [Fact]
        public void MustResolveSubscriptionFromFormattedReceiverPath()
        {
            var entityPath = ServiceBusSessionEntityPath.Create("my-topic", "my-topic/Subscriptions/my-sub");

            entityPath.IsSubscription.Should().BeTrue();
            entityPath.TopicName.Should().Be("my-topic");
            entityPath.SubscriptionName.Should().Be("my-sub");
        }

        // (b) Topic subscription resolved from the RAW receiver path — registration/discovery pass a bare
        // subscription name. Idempotent with (a): no double-format, no leftover "<topic>/Subscriptions/" prefix.
        [Fact]
        public void MustResolveSubscriptionFromRawReceiverPath()
        {
            var entityPath = ServiceBusSessionEntityPath.Create("my-topic", "my-sub");

            entityPath.IsSubscription.Should().BeTrue();
            entityPath.TopicName.Should().Be("my-topic");
            entityPath.SubscriptionName.Should().Be("my-sub");
        }

        // (c) Queue receiver — sendingPath null/empty OR equal to the receiver path both collapse to the single-arg
        // queue overload (IsSubscription false, QueueName = receiver path).
        [Fact]
        public void MustResolveQueueWhenSendingPathEmpty()
        {
            var entityPath = ServiceBusSessionEntityPath.Create(string.Empty, "my-queue");

            entityPath.IsSubscription.Should().BeFalse();
            entityPath.QueueName.Should().Be("my-queue");
        }

        [Fact]
        public void MustResolveQueueWhenSendingPathEqualsReceiverPath()
        {
            var entityPath = ServiceBusSessionEntityPath.Create("my-queue", "my-queue");

            entityPath.IsSubscription.Should().BeFalse();
            entityPath.QueueName.Should().Be("my-queue");
        }

        // (d) Prefix stripping is case-insensitive (Azure Service Bus entity-name semantics): a mixed-case formatted
        // prefix still strips to the bare subscription name.
        [Fact]
        public void MustStripFormattedPrefixCaseInsensitively()
        {
            var entityPath = ServiceBusSessionEntityPath.Create("my-topic", "My-Topic/Subscriptions/my-sub");

            entityPath.IsSubscription.Should().BeTrue();
            entityPath.TopicName.Should().Be("my-topic");
            entityPath.SubscriptionName.Should().Be("my-sub");
        }

        // (e) Wrong-arm access fails loudly rather than returning a sentinel that erases the queue/subscription
        // distinction — reading the queue name of a subscription (or vice versa) is a programming error.
        [Fact]
        public void MustThrowWhenReadingQueueNameOfSubscription()
        {
            var entityPath = ServiceBusSessionEntityPath.Create("my-topic", "my-sub");

            Action act = () => _ = entityPath.QueueName;

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void MustThrowWhenReadingTopicNameOfQueue()
        {
            var entityPath = ServiceBusSessionEntityPath.Create(string.Empty, "my-queue");

            Action act = () => _ = entityPath.TopicName;

            act.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void MustThrowWhenReadingSubscriptionNameOfQueue()
        {
            var entityPath = ServiceBusSessionEntityPath.Create(string.Empty, "my-queue");

            Action act = () => _ = entityPath.SubscriptionName;

            act.Should().Throw<InvalidOperationException>();
        }
    }
}
