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
    /// guarantee (absent, null, or boxed as any integral width, a string or a byte[] depending on the infrastructure
    /// and on whether the delivery was replayed from an outbox, which materializes it as a long), so the probe reads
    /// any INTEGRAL value whose value lies within [0, int.MaxValue] as the count, and every other shape must EXIT the
    /// delivery via the dead-letter sentinel instead of faulting the probe. An out-of-range integral is the case that
    /// makes the range check load-bearing: converting it without one throws OverflowException from inside the ladder,
    /// which is the very fault this member exists to avoid. The double used here is the only one in the suite that
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
            yield return new object[] { true, new byte[] { 0, 0, 0, 3 }, "the value is a byte[]" };
            yield return new object[] { true, 3m, "the value is a decimal, which is not an integral count" };
            yield return new object[] { true, 3d, "the value is a double, which is not an integral count" };
            yield return new object[] { true, 3f, "the value is a float, which is not an integral count" };
            yield return new object[] { true, (long)int.MaxValue + 1, "the value is an integral above int.MaxValue, which must be range-checked rather than converted" };
            yield return new object[] { true, ulong.MaxValue, "the value is an unsigned integral above int.MaxValue, which must be range-checked rather than converted" };
            yield return new object[] { true, -1L, "the value is a negative integral, which is not a count of deliveries" };
            yield return new object[] { true, -1, "the value is a negative int, which is not a count of deliveries" };
        }

        public static IEnumerable<object[]> UsableReceiveAttempts()
        {
            yield return new object[] { (sbyte)3, 3, "an sbyte" };
            yield return new object[] { (byte)3, 3, "a byte" };
            yield return new object[] { (short)3, 3, "a short" };
            yield return new object[] { (ushort)3, 3, "a ushort" };
            yield return new object[] { 3, 3, "an int" };
            yield return new object[] { 3u, 3, "a uint" };
            yield return new object[] { 3L, 3, "a long, which is how an outbox replay materializes the count" };
            yield return new object[] { 3ul, 3, "a ulong" };
            yield return new object[] { 0L, 0, "a long holding the lowest countable value" };
        }

        [Theory]
        [MemberData(nameof(UsableReceiveAttempts))]
        public async Task MustReturnTheStoredCountWhenReceiveAttemptsIsAnInRangeIntegral(object receiveAttempts, int expectedDeliveryCount, string boxedAs)
        {
            IMessagingInfrastructureReceiver sut = BuildProbe();
            var context = BuildContext(keyIsPresent: true, receiveAttempts);

            var deliveryCount = await sut.MessageDeliveryCountAsync(context, CancellationToken.None);

            deliveryCount.Should().Be(expectedDeliveryCount,
                $"Receive Attempts boxed as {boxedAs} still holds this infrastructure's own count, and deadlettering it would strand a delivery the ladder could still redeliver");
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
