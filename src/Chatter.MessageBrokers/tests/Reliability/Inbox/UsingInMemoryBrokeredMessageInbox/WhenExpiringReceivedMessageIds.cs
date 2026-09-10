using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Inbox.UsingInMemoryBrokeredMessageInbox
{
    public class WhenExpiringReceivedMessageIds : Testing.Core.Context
    {
        private const long OneMinuteInMilliseconds = 60000L;

        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly LoggerCreator<InMemoryBrokeredMessageInbox> _logger;

        // The deduplication window is an ELAPSED interval, so the inbox reads a monotonic millisecond clock rather
        // than the wall clock. Driving that clock by assignment keeps every expiry test deterministic and instant.
        private long _now;

        // Armed by the one test that needs a sweep to land between the reserve path's read of an existing entry
        // and its update of that entry. Left null everywhere else, so no other test observes it at all.
        private Action<string> _interfereBeforeReReservation;

        public WhenExpiringReceivedMessageIds()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _logger = New.Common().Logger<InMemoryBrokeredMessageInbox>();
        }

        private InMemoryBrokeredMessageInbox CreateInbox(int windowInMinutes)
            => new InMemoryBrokeredMessageInbox(_logger.Creation,
                                                new ReliabilityOptions
                                                {
                                                    InMemoryInboxDeduplicationWindowInMinutes = windowInMinutes,
                                                    InMemoryInboxMaxEntries = 200000
                                                },
                                                () => _now,
                                                id => _interfereBeforeReReservation?.Invoke(id));

        private IMessageBrokerContext CreateContext(string messageId)
        {
            var inbound = new InboundBrokeredMessage(messageId, new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", _bodyConverter.Object);
            var context = new Mock<IMessageBrokerContext>();
            context.SetupGet(c => c.BrokeredMessage).Returns(inbound);
            return context.Object;
        }

        private static async Task<bool> ReceiveReportingInvocation(InMemoryBrokeredMessageInbox inbox, IMessageBrokerContext context)
        {
            var invoked = false;
            await inbox.ReceiveViaInbox<object>(new object(), context, () => { invoked = true; return Task.CompletedTask; });
            return invoked;
        }

        // Returns the receipt task once its receiver has entered, so the caller holds a reservation that stays in
        // flight until it releases the returned source. No wall-clock wait is involved anywhere.
        private static async Task<(Task receipt, TaskCompletionSource<bool> release)> ParkAReceipt(InMemoryBrokeredMessageInbox inbox, IMessageBrokerContext context)
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var receipt = inbox.ReceiveViaInbox<object>(new object(), context, () =>
            {
                entered.SetResult(true);
                return release.Task;
            });

            await entered.Task;
            return (receipt, release);
        }

        [Fact]
        public async Task MustSkipARedeliveryInsideTheDeduplicationWindow()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            var context = CreateContext("id-1");
            await ReceiveReportingInvocation(inbox, context);

            _now = OneMinuteInMilliseconds - 1;

            (await ReceiveReportingInvocation(inbox, context)).Should().BeFalse();
        }

        [Fact]
        public async Task MustHandleARedeliveryAgainOnceTheWindowHasElapsed()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            var context = CreateContext("id-1");
            await ReceiveReportingInvocation(inbox, context);

            _now = OneMinuteInMilliseconds;

            (await ReceiveReportingInvocation(inbox, context)).Should().BeTrue();
        }

        [Fact]
        public async Task MustReportHasBeenReceivedTrueInsideTheWindowAndFalseAfterIt()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            await ReceiveReportingInvocation(inbox, CreateContext("id-1"));

            _now = OneMinuteInMilliseconds - 1;
            (await inbox.HasBeenReceived("id-1")).Should().BeTrue();

            _now = OneMinuteInMilliseconds;
            (await inbox.HasBeenReceived("id-1")).Should().BeFalse();
        }

        [Fact]
        public async Task MustHandleARedeliveryWhoseExpiredEntryASweepReclaimsWhileItIsBeingReReserved()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            var context = CreateContext("id-1");
            await ReceiveReportingInvocation(inbox, context);

            // The window has elapsed, so id-1's entry is expired and the routine sweep is due. The interference
            // below runs a receipt of an unrelated id, and that receipt's sweep reclaims id-1's expired entry -
            // between this redelivery's read of that entry and its attempt to re-reserve it.
            _now = OneMinuteInMilliseconds;
            _interfereBeforeReReservation = _ =>
            {
                _interfereBeforeReReservation = null;
                ReceiveReportingInvocation(inbox, CreateContext("sweeping-id")).GetAwaiter().GetResult();
            };

            (await ReceiveReportingInvocation(inbox, context)).Should().BeTrue();
        }

        [Fact]
        public async Task MustNotReportASweptEntryAsAnAlreadyReceivedDuplicate()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            var context = CreateContext("id-1");
            await ReceiveReportingInvocation(inbox, context);

            _now = OneMinuteInMilliseconds;
            _interfereBeforeReReservation = _ =>
            {
                _interfereBeforeReReservation = null;
                ReceiveReportingInvocation(inbox, CreateContext("sweeping-id")).GetAwaiter().GetResult();
            };
            await ReceiveReportingInvocation(inbox, context);

            _logger.VerifyWasCalled(LogLevel.Trace,
                "Brokered message of type 'Object' with id: 'id-1' was already received.",
                Times.Never());
        }

        [Fact]
        public async Task MustNeverReclaimAnInFlightReservationInsideTheWindow()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            var context = CreateContext("id-1");
            var inFlight = await ParkAReceipt(inbox, context);

            _now = OneMinuteInMilliseconds - 1;
            var duplicateInvoked = await ReceiveReportingInvocation(inbox, context);
            var receivedWhileInFlight = await inbox.HasBeenReceived("id-1");

            inFlight.release.SetResult(true);
            await inFlight.receipt;

            duplicateInvoked.Should().BeFalse();
            receivedWhileInFlight.Should().BeTrue();
        }

        [Fact]
        public async Task MustHandleARedeliveryOfAnAbandonedInFlightReservationOnceTheWindowHasElapsed()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            var context = CreateContext("id-1");
            var abandoned = await ParkAReceipt(inbox, context);

            _now = OneMinuteInMilliseconds;
            var redeliveryInvoked = await ReceiveReportingInvocation(inbox, context);

            abandoned.release.SetResult(true);
            await abandoned.receipt;

            redeliveryInvoked.Should().BeTrue();
        }

        [Fact]
        public async Task MustReportHasBeenReceivedFalseForAnAbandonedInFlightReservation()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            var abandoned = await ParkAReceipt(inbox, CreateContext("id-1"));

            _now = OneMinuteInMilliseconds;
            var receivedWhileAbandoned = await inbox.HasBeenReceived("id-1");

            abandoned.release.SetResult(true);
            await abandoned.receipt;

            receivedWhileAbandoned.Should().BeFalse();
        }

        [Fact]
        public async Task MustReclaimAnAbandonedInFlightReservationOnTheNextSweep()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            var abandoned = await ParkAReceipt(inbox, CreateContext("id-1"));

            _now = OneMinuteInMilliseconds;
            await ReceiveReportingInvocation(inbox, CreateContext("id-2"));
            var entryCountAfterTheSweep = inbox.EntryCount;

            abandoned.release.SetResult(true);
            await abandoned.receipt;

            entryCountAfterTheSweep.Should().Be(1);
        }

        /// <summary>
        /// The window is now mandatory and at least one minute, so the reclamation rule has NO configuration escape:
        /// there is no window a host can be configured with under which a completed receipt outlives it. These two
        /// theories walk the ends of the configurable domain - the shortest window the setting can express and the
        /// longest - and require the same answer at both.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(int.MaxValue)]
        public async Task MustHandleARedeliveryOnceTheWindowHasElapsedUnderEveryConfigurableWindow(int windowInMinutes)
        {
            var inbox = CreateInbox(windowInMinutes);
            var context = CreateContext("id-1");
            await ReceiveReportingInvocation(inbox, context);

            _now = windowInMinutes * OneMinuteInMilliseconds;

            (await ReceiveReportingInvocation(inbox, context)).Should().BeTrue();
            (await inbox.HasBeenReceived("id-1")).Should().BeTrue();
        }

        /// <summary>
        /// The same rule, asked of the state that used to have an escape: a reservation whose handler never returns.
        /// No configurable window leaves such a reservation held indefinitely, so a hung handler cannot make its id
        /// permanently unreclaimable under any configuration a host can start with.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(int.MaxValue)]
        public async Task MustNeverHoldAnInFlightReservationBeyondTheWindowUnderEveryConfigurableWindow(int windowInMinutes)
        {
            var inbox = CreateInbox(windowInMinutes);
            var context = CreateContext("id-1");
            var abandoned = await ParkAReceipt(inbox, context);

            _now = windowInMinutes * OneMinuteInMilliseconds;
            var receivedWhileAbandoned = await inbox.HasBeenReceived("id-1");
            var redeliveryInvoked = await ReceiveReportingInvocation(inbox, context);

            abandoned.release.SetResult(true);
            await abandoned.receipt;

            receivedWhileAbandoned.Should().BeFalse();
            redeliveryInvoked.Should().BeTrue();
        }

        [Fact]
        public async Task MustReclaimExpiredEntriesOnTheNextSweepWithoutWarning()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            await ReceiveReportingInvocation(inbox, CreateContext("id-1"));
            await ReceiveReportingInvocation(inbox, CreateContext("id-2"));

            _now = OneMinuteInMilliseconds;
            await ReceiveReportingInvocation(inbox, CreateContext("id-3"));

            inbox.EntryCount.Should().Be(1);
            _logger.VerifyWasCalled(LogLevel.Warning, null, Times.Never());
        }

        [Fact]
        public async Task MustNotSweepMoreThanOncePerSweepInterval()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            await ReceiveReportingInvocation(inbox, CreateContext("id-1"));

            _now = 10000;
            await ReceiveReportingInvocation(inbox, CreateContext("id-2"));

            // The sweep falls due one interval after construction, reclaiming id-1 and leaving id-2 unexpired.
            _now = OneMinuteInMilliseconds;
            await ReceiveReportingInvocation(inbox, CreateContext("id-3"));

            // id-2 has now expired, but the next sweep is not due for another interval, so it is still held.
            _now = OneMinuteInMilliseconds + 10000;
            await ReceiveReportingInvocation(inbox, CreateContext("id-4"));

            inbox.EntryCount.Should().Be(3);
            (await inbox.HasBeenReceived("id-2")).Should().BeFalse();
        }
    }
}
