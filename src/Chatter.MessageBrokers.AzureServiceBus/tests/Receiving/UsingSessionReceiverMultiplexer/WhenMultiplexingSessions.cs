using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingSessionReceiverMultiplexer
{
    // Pins SessionReceiverMultiplexer's arming, routing, release and teardown behavior. Every concurrency fact is
    // driven through InMemorySessionMessageReceiver's per-call TaskCompletionSources — the test decides when a child
    // yields — so no assertion depends on timing. Where a wait is unavoidable it is bounded by a generous timeout
    // and the assertion is on the outcome, never on how long it took.
    public class WhenMultiplexingSessions : Testing.Core.Context
    {
        private const string _receiverPath = "session-queue";
        private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

        private readonly List<InMemorySessionMessageReceiver> _children = new List<InMemorySessionMessageReceiver>();
        private readonly RecordingLoggerCreator<SessionReceiverMultiplexer> _logger;

        // Gates the FACTORY, not a child: the multiplexer creates a replacement child between closing the disposed
        // one and installing it, so parking the factory parks the replacement path exactly inside that window
        // without any interleaving being scheduled while the multiplexer holds its lock.
        private TaskCompletionSource<bool> _childCreationReached;
        private TaskCompletionSource<bool> _childCreationGate;

        public WhenMultiplexingSessions() => _logger = New.Common().RecordingLogger<SessionReceiverMultiplexer>();

        private SessionReceiverMultiplexer CreateSut(int maxConcurrentSessions)
            => new SessionReceiverMultiplexer(maxConcurrentSessions, CreateChild, _receiverPath, _logger.Creation);

        private IServiceBusSessionChildReceiver CreateChild()
        {
            _childCreationReached?.TrySetResult(true);
            _childCreationGate?.Task.GetAwaiter().GetResult();

            var child = new InMemorySessionMessageReceiver();
            _children.Add(child);
            return child;
        }

        private static ServiceBusReceivedMessage AnyMessage(string messageId = "message-id", string sessionId = "session-1")
            => ServiceBusMessageFactory.ReceivedMessage(messageId: messageId, sessionId: sessionId);

        private static ServiceBusReceivedMessage UnknownMessage()
            => AnyMessage("unknown-message", "unknown-session");

        [Fact]
        public void MustRejectAMaxConcurrentSessionsBelowOne()
        {
            Action act = () => CreateSut(maxConcurrentSessions: 0);

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Fact]
        public void MustCreateOneChildPerConcurrentSession()
        {
            CreateSut(maxConcurrentSessions: 3);

            _children.Should().HaveCount(3);
        }

        [Fact]
        public void MustOpenNothingUntilTheFirstReceive()
        {
            CreateSut(maxConcurrentSessions: 3);

            _children.Should().OnlyContain(child => child.ReceiveCount == 0);
        }

        [Fact]
        public async Task MustArmEveryChildOnTheFirstReceive()
        {
            var sut = CreateSut(maxConcurrentSessions: 3);

            var receive = sut.ReceiveAsync(CancellationToken.None);

            foreach (var child in _children)
            {
                await child.WaitForReceiveCountAsync(1, _waitTimeout);
            }

            receive.IsCompleted.Should().BeFalse();
        }

        [Fact]
        public async Task MustReturnTheMessageTheYieldingChildDelivered()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var expected = AnyMessage();

            var receive = sut.ReceiveAsync(CancellationToken.None);
            await _children[1].WaitForReceiveCountAsync(1, _waitTimeout);
            _children[1].YieldMessage(expected);

            (await receive).Should().BeSameAs(expected);
        }

        [Fact]
        public async Task MustServeASecondReceiveFromAnIdleSibling()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var busy = await ArrangeBusyFirstChildAsync(sut);
            var fromSibling = AnyMessage("message-b", "session-b");

            var second = sut.ReceiveAsync(CancellationToken.None);
            _children[1].YieldMessage(fromSibling);

            (await second).Should().BeSameAs(fromSibling);
            _children[0].ReceiveCount.Should().Be(1, "the child serving {0} must not be asked for another message", busy.MessageId);
        }

        [Fact]
        public async Task MustNotRearmAChildWhileItIsServingADelivery()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            await ArrangeBusyFirstChildAsync(sut);

            var second = sut.ReceiveAsync(CancellationToken.None);

            // The sibling yielding nothing forces the loop through another arming pass with the first child still
            // serving its delivery: the sibling is re-armed, so its second receive is proof the pass happened.
            _children[1].YieldNoMessage();
            await _children[1].WaitForReceiveCountAsync(2, _waitTimeout);

            _children[0].ReceiveCount.Should().Be(1);
            second.IsCompleted.Should().BeFalse();
        }

        [Fact]
        public async Task MustRearmAFreedChildWhileTheLoopWaitsOnASibling()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var busy = await ArrangeBusyFirstChildAsync(sut);
            var next = AnyMessage("message-a2");

            var second = sut.ReceiveAsync(CancellationToken.None);
            _children[1].YieldNoMessage();
            await _children[1].WaitForReceiveCountAsync(2, _waitTimeout);

            sut.DeliveryReleased(busy);

            // The loop is parked on the sibling's pending receive. Freeing the first child must re-arm it there and
            // then, without waiting for the sibling to yield anything.
            await _children[0].WaitForReceiveCountAsync(2, _waitTimeout);
            _children[0].YieldMessage(next);

            (await second).Should().BeSameAs(next);
            _children[1].OutstandingReceiveCount.Should().Be(1, "the sibling's receive was never completed");
        }

        [Fact]
        public async Task MustCancelAParkedReceiveWithTheCallersToken()
        {
            var sut = CreateSut(maxConcurrentSessions: 1);
            var cancellation = new CancellationTokenSource();

            var receive = sut.ReceiveAsync(cancellation.Token);
            await _children[0].WaitForReceiveCountAsync(1, _waitTimeout);
            cancellation.Cancel();

            var first = await Task.WhenAny(receive, Task.Delay(_waitTimeout));
            first.Should().BeSameAs(receive, "a parked receive must observe the caller's cancellation token");
            Func<Task> act = () => receive;
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Fact]
        public async Task MustCompleteOnTheChildThatDeliveredTheMessage()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var delivered = await ArrangeBusyFirstChildAsync(sut);

            await sut.CompleteAsync(delivered);

            _children[0].CompletedMessages.Should().ContainSingle().Which.Should().BeSameAs(delivered);
            _children[1].CompletedMessages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustAbandonOnTheChildThatDeliveredTheMessage()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var delivered = await ArrangeBusyFirstChildAsync(sut);
            var propertiesToModify = new Dictionary<string, object> { ["reason"] = "nack" };

            await sut.AbandonAsync(delivered, propertiesToModify);

            _children[0].AbandonedMessages.Should().ContainSingle().Which.Should().BeSameAs(delivered);
            _children[0].AbandonPropertiesToModify.Should().ContainSingle().Which.Should().BeSameAs(propertiesToModify);
            _children[1].AbandonedMessages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustDeadLetterOnTheChildThatDeliveredTheMessage()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var delivered = await ArrangeBusyFirstChildAsync(sut);

            await sut.DeadLetterAsync(delivered, "reason", "description");

            _children[0].DeadLetteredMessages.Should().ContainSingle()
                .Which.Should().Be((delivered, "reason", "description"));
            _children[1].DeadLetteredMessages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustThrowWhenCompletingAMessageNoChildHolds()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);

            Func<Task> act = () => sut.CompleteAsync(UnknownMessage());

            // A completed task would mean SUCCESS and have the receiver report a settlement that never happened.
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*{_receiverPath}*")
                .WithMessage("*unknown-message*");
        }

        [Fact]
        public async Task MustThrowWhenAbandoningAMessageNoChildHolds()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);

            Func<Task> act = () => sut.AbandonAsync(UnknownMessage(), new Dictionary<string, object>());

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*{_receiverPath}*")
                .WithMessage("*unknown-message*");
        }

        [Fact]
        public async Task MustThrowWhenDeadLetteringAMessageNoChildHolds()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);

            Func<Task> act = () => sut.DeadLetterAsync(UnknownMessage(), "reason", "description");

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*{_receiverPath}*")
                .WithMessage("*unknown-message*");
        }

        [Fact]
        public async Task MustIgnoreADeliveryReleaseForAMessageNoChildHolds()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            await ArrangeBusyFirstChildAsync(sut);

            // A cancelled worker still runs the finally that raises this signal, so an unknown message must not fault.
            Action act = () => sut.DeliveryReleased(UnknownMessage());

            act.Should().NotThrow();
            _children[0].ReceiveCount.Should().Be(1, "the busy child's slot must not be freed by an unknown release");
        }

        [Fact]
        public async Task MustAnswerNoSessionReceiverForAMessageNoChildHolds()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            await ArrangeBusyFirstChildAsync(sut);

            sut.SessionReceiverFor(UnknownMessage()).Should().BeNull();
        }

        [Fact]
        public async Task MustRearmAChildThatYieldsNoMessage()
        {
            var sut = CreateSut(maxConcurrentSessions: 1);
            var delivered = AnyMessage();

            var receive = sut.ReceiveAsync(CancellationToken.None);
            await _children[0].WaitForReceiveCountAsync(1, _waitTimeout);
            _children[0].YieldNoMessage();
            await _children[0].WaitForReceiveCountAsync(2, _waitTimeout);
            _children[0].YieldMessage(delivered);

            (await receive).Should().BeSameAs(delivered);
        }

        [Fact]
        public void MustNotReportClosedWhenAChildReportsClosed()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);

            _children[0].IsClosedOrClosing = true;

            // Reporting closed here would discard the healthy siblings with the disposed child.
            sut.IsClosedOrClosing.Should().BeFalse();
        }

        [Fact]
        public async Task MustCloseEveryChild()
        {
            var sut = CreateSut(maxConcurrentSessions: 3);

            await sut.CloseAsync();

            sut.IsClosedOrClosing.Should().BeTrue();
            _children.Should().OnlyContain(child => child.CloseCount == 1);
        }

        [Fact]
        public async Task MustCloseIdempotently()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);

            await sut.CloseAsync();
            Func<Task> secondClose = () => sut.CloseAsync();

            await secondClose.Should().NotThrowAsync();
            _children.Should().OnlyContain(child => child.CloseCount == 1);
        }

        [Fact]
        public async Task MustYieldNoMessageAfterClose()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);

            await sut.CloseAsync();

            (await sut.ReceiveAsync(CancellationToken.None)).Should().BeNull();
            _children.Should().OnlyContain(child => child.ReceiveCount == 0);
        }

        [Fact]
        public async Task MustWakeAParkedReceiveOnClose()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var receive = sut.ReceiveAsync(CancellationToken.None);
            foreach (var child in _children)
            {
                await child.WaitForReceiveCountAsync(1, _waitTimeout);
            }

            await sut.CloseAsync();

            var first = await Task.WhenAny(receive, Task.Delay(_waitTimeout));
            first.Should().BeSameAs(receive, "closing must wake a parked receive rather than leave teardown waiting");
            (await receive).Should().BeNull();
        }

        [Fact]
        public async Task MustReplaceAChildThatWasDisposedWhileClosing()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var receive = await ArrangeArmedReceiveAsync(sut);

            _children[0].IsClosedOrClosing = true;
            _children[0].FailReceive(new ObjectDisposedException("receiver"));

            (await receive).Should().BeNull();
            _children[0].CloseCount.Should().Be(1);
            _children.Should().HaveCount(3, "the disposed child is replaced from the factory, the healthy sibling is not");
            _logger.CountOf(LogLevel.Warning).Should().Be(1);
        }

        [Fact]
        public async Task MustCloseRatherThanInstallAReplacementForAMultiplexerThatClosedMeanwhile()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var receive = await ArrangeArmedReceiveAsync(sut);
            _childCreationReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _childCreationGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _children[0].IsClosedOrClosing = true;
            _children[0].FailReceive(new ObjectDisposedException("receiver"));

            // The replacement is parked mid-creation, so teardown runs to completion strictly inside the window
            // between the disposed child's close and the install.
            var parked = await Task.WhenAny(_childCreationReached.Task, Task.Delay(_waitTimeout));
            parked.Should().BeSameAs(_childCreationReached.Task, "the replacement child is created before it is installed");
            await sut.CloseAsync();
            _childCreationGate.SetResult(true);

            (await receive).Should().BeNull();
            _children.Should().HaveCount(3);
            _children[2].CloseCount.Should().Be(1,
                "CloseAsync is idempotent, so a replacement installed after teardown would be a child no close path ever reaches");
        }

        [Fact]
        public async Task MustLeaveSiblingsArmedWhenAChildIsReplaced()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var receive = await ArrangeArmedReceiveAsync(sut);

            _children[0].IsClosedOrClosing = true;
            _children[0].FailReceive(new ObjectDisposedException("receiver"));
            await receive;

            _children[1].ReceiveCount.Should().Be(1);
            _children[1].OutstandingReceiveCount.Should().Be(1, "the healthy sibling's pending receive must survive");
        }

        [Fact]
        public async Task MustArmTheReplacementChildOnTheNextReceive()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var receive = await ArrangeArmedReceiveAsync(sut);
            _children[0].IsClosedOrClosing = true;
            _children[0].FailReceive(new ObjectDisposedException("receiver"));
            await receive;

            var second = sut.ReceiveAsync(CancellationToken.None);

            await _children[2].WaitForReceiveCountAsync(1, _waitTimeout);
            second.IsCompleted.Should().BeFalse();
        }

        [Fact]
        public async Task MustNotReplaceAChildThatIsNotClosing()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var receive = await ArrangeArmedReceiveAsync(sut);
            var disposed = new ObjectDisposedException("receiver");

            // Disposal of something OTHER than a closing child is not the rebuild condition; it belongs to the
            // receiver's existing recovery ladder like any other fault.
            _children[0].FailReceive(disposed);

            Func<Task> act = () => receive;
            (await act.Should().ThrowAsync<ObjectDisposedException>()).Which.Should().BeSameAs(disposed);
            _children.Should().HaveCount(2);
        }

        [Fact]
        public async Task MustPropagateANonDisposalFault()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var receive = await ArrangeArmedReceiveAsync(sut);
            var fault = new InvalidOperationException("boom");

            _children[0].FailReceive(fault);

            Func<Task> act = () => receive;
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
            _children[1].OutstandingReceiveCount.Should().Be(1, "a sibling's pending receive must survive a fault");
        }

        [Fact]
        public async Task MustRearmAChildWhoseReceiveFaulted()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var receive = await ArrangeArmedReceiveAsync(sut);
            _children[0].FailReceive(new InvalidOperationException("boom"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => receive);

            var second = sut.ReceiveAsync(CancellationToken.None);

            await _children[0].WaitForReceiveCountAsync(2, _waitTimeout);
            second.IsCompleted.Should().BeFalse();
        }

        [Fact]
        public async Task MustWarnOnceWhileASessionHoldOutlivesItsSessionLock()
        {
            var sut = CreateSut(maxConcurrentSessions: 3);
            await ArrangeBusyFirstChildAsync(sut);
            _children[0].HeldSessionId = "session-a";
            _children[0].HeldSessionLockedUntil = DateTimeOffset.UtcNow.AddMinutes(-1);

            // Two further arming passes over the same busy episode, each driven by a different sibling yielding.
            var second = sut.ReceiveAsync(CancellationToken.None);
            _children[1].YieldMessage(AnyMessage("message-b", "session-b"));
            await second;
            var third = sut.ReceiveAsync(CancellationToken.None);
            _children[2].YieldMessage(AnyMessage("message-c", "session-c"));
            await third;

            _logger.CountOf(LogLevel.Warning).Should().Be(1);
            _logger.LoggedMessages.Should().ContainSingle(logged => logged.level == LogLevel.Warning)
                .Which.message.Should().Contain("session-a").And.Contain(_receiverPath);
        }

        [Fact]
        public async Task MustNotReclaimASessionHoldThatOutlivedItsSessionLock()
        {
            var sut = CreateSut(maxConcurrentSessions: 2);
            var delivered = await ArrangeBusyFirstChildAsync(sut);
            _children[0].HeldSessionId = "session-a";
            _children[0].HeldSessionLockedUntil = DateTimeOffset.UtcNow.AddMinutes(-1);

            var second = sut.ReceiveAsync(CancellationToken.None);
            _children[1].YieldMessage(AnyMessage("message-b", "session-b"));
            await second;

            // The stale-lock backstop is LOG-ONLY: freeing the slot here would race the live worker and leave its
            // settlement unroutable.
            _children[0].ReceiveCount.Should().Be(1);
            await sut.CompleteAsync(delivered);
            _children[0].CompletedMessages.Should().ContainSingle().Which.Should().BeSameAs(delivered);
        }

        // Starts a receive and waits until every child has been armed, returning the in-flight receive.
        private async Task<Task<ServiceBusReceivedMessage>> ArrangeArmedReceiveAsync(SessionReceiverMultiplexer sut)
        {
            var receive = sut.ReceiveAsync(CancellationToken.None);
            foreach (var child in _children)
            {
                await child.WaitForReceiveCountAsync(1, _waitTimeout);
            }

            return receive;
        }

        // Drives the multiplexer until its FIRST child is serving a delivery and every child has been armed once.
        private async Task<ServiceBusReceivedMessage> ArrangeBusyFirstChildAsync(SessionReceiverMultiplexer sut)
        {
            var delivered = AnyMessage("message-a", "session-a");
            var receive = await ArrangeArmedReceiveAsync(sut);

            _children[0].YieldMessage(delivered);
            (await receive).Should().BeSameAs(delivered);
            return delivered;
        }
    }
}
