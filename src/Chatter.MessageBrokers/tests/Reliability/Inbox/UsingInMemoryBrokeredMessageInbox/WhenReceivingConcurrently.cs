using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Inbox.UsingInMemoryBrokeredMessageInbox
{
    public class WhenReceivingConcurrently : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly LoggerCreator<InMemoryBrokeredMessageInbox> _logger;
        private readonly InMemoryBrokeredMessageInbox _sut;
        private readonly IMessageBrokerContext _context;
        private readonly TaskCompletionSource<bool> _receiverEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _receiverRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _receiverInvocations;

        public WhenReceivingConcurrently()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _logger = New.Common().Logger<InMemoryBrokeredMessageInbox>();
            _sut = new InMemoryBrokeredMessageInbox(_logger.Creation, new ReliabilityOptions
            {
                InMemoryInboxDeduplicationWindowInMinutes = 60,
                InMemoryInboxMaxEntries = 200000
            });

            var inbound = new InboundBrokeredMessage("id-1", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", _bodyConverter.Object);
            var context = new Mock<IMessageBrokerContext>();
            context.SetupGet(c => c.BrokeredMessage).Returns(inbound);
            _context = context.Object;
        }

        // INVARIANT: only the first invocation parks, so the duplicate always arrives while the
        // first receipt is still in flight; a second invocation completes immediately so a
        // regression that invokes the receiver twice fails the assertion instead of deadlocking.
        private Task ParkFirstInvocation()
        {
            if (Interlocked.Increment(ref _receiverInvocations) > 1)
            {
                return Task.CompletedTask;
            }

            _receiverEntered.SetResult(true);
            return _receiverRelease.Task;
        }

        private async Task ReceiveSameMessageIdConcurrently()
        {
            var inFlightReceipt = _sut.ReceiveViaInbox<object>(new object(), _context, ParkFirstInvocation);
            await _receiverEntered.Task;

            var duplicateReceipt = _sut.ReceiveViaInbox<object>(new object(), _context, ParkFirstInvocation);
            await duplicateReceipt;

            _receiverRelease.SetResult(true);
            await inFlightReceipt;
        }

        [Fact]
        public async Task MustInvokeMessageReceiverExactlyOnceForConcurrentReceiptsOfOneMessageId()
        {
            await ReceiveSameMessageIdConcurrently();
            _receiverInvocations.Should().Be(1);
        }

        [Fact]
        public async Task MustNotThrowFromEitherReceiptWhenOneMessageIdIsReceivedConcurrently()
            => await FluentActions.Invoking(ReceiveSameMessageIdConcurrently).Should().NotThrowAsync();

        [Fact]
        public async Task MustLogTraceForAlreadyReceivedMessageWhenReservationIsLost()
        {
            await ReceiveSameMessageIdConcurrently();

            _logger.VerifyWasCalled(LogLevel.Trace,
                "Brokered message of type 'Object' with id: 'id-1' was already received.",
                Times.Once());
        }
    }
}
