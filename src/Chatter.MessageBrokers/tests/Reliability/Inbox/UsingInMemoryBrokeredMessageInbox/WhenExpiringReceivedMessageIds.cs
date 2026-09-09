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
    public class WhenExpiringReceivedMessageIds : Testing.Core.Context
    {
        private const long OneMinuteInMilliseconds = 60000L;

        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly LoggerCreator<InMemoryBrokeredMessageInbox> _logger;

        // The deduplication window is an ELAPSED interval, so the inbox reads a monotonic millisecond clock rather
        // than the wall clock. Driving that clock by assignment keeps every expiry test deterministic and instant.
        private long _now;

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
                                                () => _now);

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
        public async Task MustNeverExpireAnInFlightReservation()
        {
            var inbox = CreateInbox(windowInMinutes: 1);
            var context = CreateContext("id-1");
            var receiverEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receiverRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var inFlightReceipt = inbox.ReceiveViaInbox<object>(new object(), context, () =>
            {
                receiverEntered.SetResult(true);
                return receiverRelease.Task;
            });
            await receiverEntered.Task;

            _now = OneMinuteInMilliseconds * 1000;
            var duplicateInvoked = await ReceiveReportingInvocation(inbox, context);
            var receivedWhileInFlight = await inbox.HasBeenReceived("id-1");

            receiverRelease.SetResult(true);
            await inFlightReceipt;

            duplicateInvoked.Should().BeFalse();
            receivedWhileInFlight.Should().BeTrue();
        }

        [Fact]
        public async Task MustNeverExpireWhenTheWindowIsDisabled()
        {
            var inbox = CreateInbox(windowInMinutes: 0);
            var context = CreateContext("id-1");
            await ReceiveReportingInvocation(inbox, context);

            _now = long.MaxValue;

            (await ReceiveReportingInvocation(inbox, context)).Should().BeFalse();
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
