using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Inbox.UsingInMemoryBrokeredMessageInbox
{
    public class WhenEvictingAtTheEntryCap : Testing.Core.Context
    {
        private const long OneMinuteInMilliseconds = 60000L;

        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly LoggerCreator<InMemoryBrokeredMessageInbox> _logger;
        private long _now;

        public WhenEvictingAtTheEntryCap()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _logger = New.Common().Logger<InMemoryBrokeredMessageInbox>();
        }

        private InMemoryBrokeredMessageInbox CreateInbox(int maxEntries, int windowInMinutes = 60)
            => new InMemoryBrokeredMessageInbox(_logger.Creation,
                                                new ReliabilityOptions
                                                {
                                                    InMemoryInboxDeduplicationWindowInMinutes = windowInMinutes,
                                                    InMemoryInboxMaxEntries = maxEntries
                                                },
                                                () => _now);

        private IMessageBrokerContext CreateContext(string messageId)
        {
            var inbound = new InboundBrokeredMessage(messageId, new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", _bodyConverter.Object);
            var context = new Mock<IMessageBrokerContext>();
            context.SetupGet(c => c.BrokeredMessage).Returns(inbound);
            return context.Object;
        }

        private async Task<bool> ReceiveReportingInvocation(InMemoryBrokeredMessageInbox inbox, string messageId)
        {
            var invoked = false;
            await inbox.ReceiveViaInbox<object>(new object(), CreateContext(messageId), () => { invoked = true; return Task.CompletedTask; });
            return invoked;
        }

        // Returns the receipt task once its receiver has entered, so the caller holds a reservation that stays in
        // flight until it releases the returned source. No wall-clock wait is involved anywhere.
        private async Task<(Task receipt, TaskCompletionSource<bool> release)> ParkAReceipt(InMemoryBrokeredMessageInbox inbox, string messageId)
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var receipt = inbox.ReceiveViaInbox<object>(new object(), CreateContext(messageId), () =>
            {
                entered.SetResult(true);
                return release.Task;
            });

            await entered.Task;
            return (receipt, release);
        }

        [Fact]
        public async Task MustEvictACompletedEntryWhenTheCapIsReached()
        {
            var inbox = CreateInbox(maxEntries: 1);
            await ReceiveReportingInvocation(inbox, "id-1");

            await ReceiveReportingInvocation(inbox, "id-2");

            inbox.EntryCount.Should().Be(1);
            (await inbox.HasBeenReceived("id-1")).Should().BeFalse();
        }

        [Fact]
        public async Task MustWarnExactlyOnceAcrossRepeatedCapEvictions()
        {
            var inbox = CreateInbox(maxEntries: 1);
            await ReceiveReportingInvocation(inbox, "id-1");
            await ReceiveReportingInvocation(inbox, "id-2");
            await ReceiveReportingInvocation(inbox, "id-3");
            await ReceiveReportingInvocation(inbox, "id-4");

            _logger.VerifyWasCalled(LogLevel.Warning,
                "The in memory inbox reached its cap of 1 received message id(s) and evicted 1 unexpired one(s). "
                + "Its configured deduplication window (60 minutes) is no longer being honoured under this load, so a "
                + "redelivery inside that window can be handled again. Raise InMemoryInboxMaxEntries or shorten "
                + "InMemoryInboxDeduplicationWindowInMinutes.",
                Times.Once());
        }

        [Fact]
        public async Task MustHandleAnEvictedIdAgainOnRedelivery()
        {
            var inbox = CreateInbox(maxEntries: 1);
            await ReceiveReportingInvocation(inbox, "id-1");
            await ReceiveReportingInvocation(inbox, "id-2");

            (await ReceiveReportingInvocation(inbox, "id-1")).Should().BeTrue();
        }

        [Fact]
        public async Task MustReclaimExpiredEntriesBeforeEvictingUnexpiredOnesAndNotWarn()
        {
            var inbox = CreateInbox(maxEntries: 2, windowInMinutes: 1);
            await ReceiveReportingInvocation(inbox, "id-1");

            _now = 10000;
            await ReceiveReportingInvocation(inbox, "id-2");

            // The routine sweep falls due here and reclaims id-1 only; id-2 is still inside its window.
            _now = OneMinuteInMilliseconds;
            await ReceiveReportingInvocation(inbox, "id-3");

            // id-4 puts the store over its cap while the next routine sweep is not yet due, so the cap runs the
            // sweep itself and the newly expired id-2 covers the overage without any unexpired entry being evicted.
            _now = OneMinuteInMilliseconds + 10000;
            await ReceiveReportingInvocation(inbox, "id-4");

            inbox.EntryCount.Should().Be(2);
            (await inbox.HasBeenReceived("id-3")).Should().BeTrue();
            _logger.VerifyWasCalled(LogLevel.Warning, null, Times.Never());
        }

        [Fact]
        public async Task MustGrowPastTheCapWithoutThrowingWhenEveryEntryIsInFlight()
        {
            var inbox = CreateInbox(maxEntries: 1);

            var first = await ParkAReceipt(inbox, "id-1");
            var second = await ParkAReceipt(inbox, "id-2");
            var third = await ParkAReceipt(inbox, "id-3");

            inbox.EntryCount.Should().Be(3);
            _logger.VerifyWasCalled(LogLevel.Warning, null, Times.Never());

            first.release.SetResult(true);
            second.release.SetResult(true);
            third.release.SetResult(true);
            await Task.WhenAll(first.receipt, second.receipt, third.receipt);
        }

        [Fact]
        public async Task MustReclaimAnAbandonedInFlightReservationRatherThanEvictAnUnexpiredReceiptAtTheCap()
        {
            // A two minute window makes the routine sweep interval (capped at one minute) shorter than the window,
            // which is what lets the store reach a moment where an entry is abandoned but the next routine sweep
            // is not yet due - so enforcing the cap is what has to reclaim it.
            var inbox = CreateInbox(maxEntries: 3, windowInMinutes: 2);

            _now = 10000;
            var abandoned = await ParkAReceipt(inbox, "id-1");

            _now = OneMinuteInMilliseconds;
            await ReceiveReportingInvocation(inbox, "id-2");

            _now = 2 * OneMinuteInMilliseconds;
            await ReceiveReportingInvocation(inbox, "id-3");

            // id-1's reservation is now older than the window, and id-4 puts the store one over its cap.
            _now = 2 * OneMinuteInMilliseconds + 10000;
            await ReceiveReportingInvocation(inbox, "id-4");
            var entryCountAfterTheCapWasEnforced = inbox.EntryCount;

            abandoned.release.SetResult(true);
            await abandoned.receipt;

            entryCountAfterTheCapWasEnforced.Should().Be(3);
            (await inbox.HasBeenReceived("id-1")).Should().BeFalse();
            (await inbox.HasBeenReceived("id-2")).Should().BeTrue();
            (await inbox.HasBeenReceived("id-3")).Should().BeTrue();
            _logger.VerifyWasCalled(LogLevel.Warning, null, Times.Never());
        }

        [Fact]
        public async Task MustNeverEvictAnInFlightReservationAtTheCap()
        {
            var inbox = CreateInbox(maxEntries: 1);
            await ReceiveReportingInvocation(inbox, "id-1");

            var inFlight = await ParkAReceipt(inbox, "id-2");

            var duplicateInvoked = await ReceiveReportingInvocation(inbox, "id-2");
            var receivedWhileInFlight = await inbox.HasBeenReceived("id-2");
            var entryCountWhileInFlight = inbox.EntryCount;

            inFlight.release.SetResult(true);
            await inFlight.receipt;

            duplicateInvoked.Should().BeFalse();
            receivedWhileInFlight.Should().BeTrue();
            entryCountWhileInFlight.Should().Be(1);
        }
    }
}
