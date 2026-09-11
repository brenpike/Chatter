using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingMessagingInfrastructureReceiver
{
    /// <summary>
    /// Pins the PUBLISHED default of <see cref="IMessagingInfrastructureReceiver.MessageDeliveryCountAsync"/> — the
    /// delivery-count probe every infrastructure that does NOT declare the member inherits.
    /// </summary>
    /// <remarks>
    /// INVARIANT: the probe is awaited inside the Brokered Message Receiver's generic error ladder, so a probe that
    /// THROWS aborts the settlement that the ladder was about to make — the delivery is neither negatively
    /// acknowledged nor deadlettered and redelivers forever. Receive Attempts arrives off the wire with no type
    /// guarantee (absent, null, or boxed as a long, a string or a byte[] depending on the infrastructure and on
    /// whether the delivery was replayed from an outbox), so every unusable shape must EXIT the delivery via the
    /// dead-letter sentinel instead of faulting the probe. The double used here is the only one in the suite that
    /// inherits the member rather than declaring it.
    /// </remarks>
    public class WhenProbingDeliveryCount : Testing.Core.Context
    {
        // The count that sends a delivery down the ladder's deadletter branch for any MaxReceiveAttempts.
        private const int DeadLetterSentinel = int.MaxValue;

        public static IEnumerable<object[]> UnusableReceiveAttempts()
        {
            yield return new object[] { false, null, "the key is absent" };
            yield return new object[] { true, null, "the value is null" };
            yield return new object[] { true, "3", "the value is a string" };
            yield return new object[] { true, 3L, "the value is boxed as a long" };
            yield return new object[] { true, new byte[] { 0, 0, 0, 3 }, "the value is a byte[]" };
        }

        [Theory]
        [MemberData(nameof(UnusableReceiveAttempts))]
        public async Task MustReturnTheDeadLetterSentinelWhenReceiveAttemptsIsUnusable(bool keyIsPresent, object receiveAttempts, string because)
        {
            IMessagingInfrastructureReceiver sut = BuildProbe();
            var context = BuildContext(keyIsPresent, receiveAttempts);

            var deliveryCount = await sut.MessageDeliveryCountAsync(context, CancellationToken.None);

            deliveryCount.Should().Be(DeadLetterSentinel,
                $"the probe cannot read a delivery count when {because}, and throwing there would abort the settlement and redeliver the message forever");
        }

        [Fact]
        public async Task MustReturnTheStoredCountWhenReceiveAttemptsIsAnInt()
        {
            IMessagingInfrastructureReceiver sut = BuildProbe();
            var context = BuildContext(keyIsPresent: true, receiveAttempts: 4);

            var deliveryCount = await sut.MessageDeliveryCountAsync(context, CancellationToken.None);

            deliveryCount.Should().Be(4, "a genuine int Receive Attempts is the count the infrastructure recorded and the ladder must see it unchanged");
        }

        [Fact]
        public async Task MustReturnTheDeadLetterSentinelWhenThereIsNoContext()
        {
            IMessagingInfrastructureReceiver sut = BuildProbe();

            var deliveryCount = await sut.MessageDeliveryCountAsync(null, CancellationToken.None);

            deliveryCount.Should().Be(DeadLetterSentinel,
                "a probe with nothing to read must still answer, because the ladder has no other way to settle the delivery");
        }

        private static IMessagingInfrastructureReceiver BuildProbe()
            => new InheritsTheDefaultDeliveryCountProbe(new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 0));

        private static MessageBrokerContext BuildContext(bool keyIsPresent, object receiveAttempts)
        {
            var converter = new JsonBodyConverter();
            var applicationProperties = new Dictionary<string, object>();

            if (keyIsPresent)
            {
                applicationProperties[MessageContext.ReceiveAttempts] = receiveAttempts;
            }

            return new MessageBrokerContext(
                messageId: Guid.NewGuid().ToString(),
                body: converter.Convert(new UnusedBody()),
                applicationProperties: applicationProperties,
                messageReceiverPath: "test-queue",
                receiverCancellationToken: CancellationToken.None,
                bodyConverter: converter);
        }

        private class UnusedBody : CQRS.IMessage
        {
        }
    }
}
