using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingSessionMessageReceiverAdapter
{
    // Pins the connection-free behavior of AzureSdkSessionMessageReceiverAdapter that is reachable without a
    // live Azure Service Bus namespace. ServiceBusSessionReceiver is SEALED and is only acquired via the live
    // client's AcceptNextSessionAsync, so the held-session settle/roll paths cannot be exercised here; what is
    // observable is the guard behavior BEFORE a session is held: no session is held on construction, settle
    // calls short-circuit (no held session and/or ReceiveAndDelete mode), and CloseAsync flips IsClosedOrClosing
    // without touching a session. The held-session FIFO/idle/lock-loss rollover paths require a live session and
    // are documented in the worker report as untestable behind this seam.
    public class WhenSettlingWithoutHeldSession : Testing.Core.Context
    {
        private const string _receiverPath = "session-queue";
        private const string _connectionString =
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret";

        // A placeholder SAS connection string opens no connection (the SDK connects lazily), so this is a valid
        // stand-in for the shared client the adapter would receive from DI.
        private static ServiceBusClient CreateClient() => new ServiceBusClient(_connectionString);

        private static AzureSdkSessionMessageReceiverAdapter CreateSut(ServiceBusReceiveMode receiveMode = ServiceBusReceiveMode.PeekLock)
            => new AzureSdkSessionMessageReceiverAdapter(CreateClient(),
                                                         ServiceBusSessionEntityPath.Create(string.Empty, _receiverPath),
                                                         receiveMode,
                                                         prefetchCount: 0,
                                                         sessionIdleTimeout: TimeSpan.FromSeconds(1),
                                                         maxSessionLockRenewalDuration: TimeSpan.FromMinutes(5),
                                                         Mock.Of<ILogger>());

        private static ServiceBusReceivedMessage AnyMessage()
            => ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: new BinaryData(new byte[] { 1 }),
                messageId: "message-id",
                lockTokenGuid: Guid.NewGuid());

        [Fact]
        public void MustHoldNoSessionOnConstruction()
        {
            var sut = CreateSut();
            sut.HeldSessionReceiver.Should().BeNull();
        }

        [Fact]
        public void MustExposeNoSessionIdWhenNoSessionHeld()
        {
            var sut = CreateSut();
            sut.HeldSessionId.Should().BeNull();
        }

        [Fact]
        public void MustExposeNoSessionLockExpiryWhenNoSessionHeld()
        {
            var sut = CreateSut();
            sut.HeldSessionLockedUntil.Should().BeNull();
        }

        [Fact]
        public void MustAnswerNoSessionReceiverForAMessageWhenNoSessionHeld()
        {
            var sut = CreateSut();

            // The single-session adapter answers with the session it holds, so with none held there is no
            // session receiver to put in the message's transaction container.
            sut.SessionReceiverFor(AnyMessage()).Should().BeNull();
        }

        [Fact]
        public void MustNoOpDeliveryReleasedWhenNoSessionHeld()
        {
            var sut = CreateSut();

            // Holding one session there is no slot to free, so the release signal is inert — and it must stay
            // inert with no session held at all, since the signal arrives after settlement has already rolled
            // the session away.
            Action act = () => sut.DeliveryReleased(AnyMessage());

            act.Should().NotThrow();
        }

        [Fact]
        public void MustSatisfyBothSessionReceiverPorts()
        {
            var sut = CreateSut();

            sut.Should().BeAssignableTo<IServiceBusSessionMessageReceiver>();
            sut.Should().BeAssignableTo<IServiceBusSessionChildReceiver>();
        }

        [Fact]
        public void MustNotReportClosedOnConstruction()
        {
            var sut = CreateSut();
            sut.IsClosedOrClosing.Should().BeFalse();
        }

        [Fact]
        public async Task MustReportClosedAfterClose()
        {
            var sut = CreateSut();

            await sut.CloseAsync();

            sut.IsClosedOrClosing.Should().BeTrue();
        }

        [Fact]
        public async Task MustReportCompleteUnreachableWhenNoSessionHeld()
        {
            var sut = CreateSut();

            // No held session under PeekLock: the delivery's lock is still held by the broker and this adapter
            // can no longer reach it. Reporting Settled here would have the receiver record an acknowledgement
            // that never happened, so the absence is reported as DeliveryUnreachable.
            var outcome = await sut.CompleteAsync(AnyMessage());

            outcome.Should().Be(ServiceBusSettlementOutcome.DeliveryUnreachable);
        }

        [Fact]
        public async Task MustReportAbandonUnreachableWhenNoSessionHeld()
        {
            var sut = CreateSut();

            var outcome = await sut.AbandonAsync(AnyMessage(), new Dictionary<string, object>());

            outcome.Should().Be(ServiceBusSettlementOutcome.DeliveryUnreachable);
        }

        [Fact]
        public async Task MustReportDeadLetterUnreachableWhenNoSessionHeld()
        {
            // Materially the worst of the three: a poison message reported as contained while it is still in
            // circulation. The released session must be reported, never papered over.
            var sut = CreateSut();

            var outcome = await sut.DeadLetterAsync(AnyMessage(), "reason", "description");

            outcome.Should().Be(ServiceBusSettlementOutcome.DeliveryUnreachable);
        }

        [Fact]
        public async Task MustReportSettlementNotOwedInReceiveAndDeleteMode()
        {
            // In ReceiveAndDelete the settle calls short-circuit on receive-mode before any session lookup:
            // Azure Service Bus removed the delivery on receipt, so no settlement was ever owed. That is a
            // DIFFERENT answer from an unreachable delivery, which is why the port reports three states.
            var sut = CreateSut(ServiceBusReceiveMode.ReceiveAndDelete);

            var complete = await sut.CompleteAsync(AnyMessage());
            var abandon = await sut.AbandonAsync(AnyMessage(), new Dictionary<string, object>());
            var deadLetter = await sut.DeadLetterAsync(AnyMessage(), "reason", "description");

            complete.Should().Be(ServiceBusSettlementOutcome.NotOwed);
            abandon.Should().Be(ServiceBusSettlementOutcome.NotOwed);
            deadLetter.Should().Be(ServiceBusSettlementOutcome.NotOwed);
        }

        [Fact]
        public async Task MustCloseIdempotentlyWhenNoSessionHeld()
        {
            var sut = CreateSut();

            await sut.CloseAsync();
            Func<Task> secondClose = () => sut.CloseAsync();

            await secondClose.Should().NotThrowAsync();
        }
    }
}
