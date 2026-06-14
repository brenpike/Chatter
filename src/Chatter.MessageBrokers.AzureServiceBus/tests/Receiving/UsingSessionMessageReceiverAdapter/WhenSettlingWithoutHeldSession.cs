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
                                                         _receiverPath,
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
        public async Task MustNoOpCompleteWhenNoSessionHeld()
        {
            var sut = CreateSut();

            // No held session: CompleteAsync short-circuits to a completed task rather than touching a session
            // receiver (which would require a live connection).
            Func<Task> act = () => sut.CompleteAsync(AnyMessage());

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustNoOpAbandonWhenNoSessionHeld()
        {
            var sut = CreateSut();

            Func<Task> act = () => sut.AbandonAsync(AnyMessage(), new Dictionary<string, object>());

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustNoOpDeadLetterWhenNoSessionHeld()
        {
            var sut = CreateSut();

            Func<Task> act = () => sut.DeadLetterAsync(AnyMessage(), "reason", "description");

            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task MustNoOpSettleInReceiveAndDeleteMode()
        {
            // In ReceiveAndDelete the settle calls short-circuit on receive-mode before any session lookup,
            // so they complete without a connection even conceptually.
            var sut = CreateSut(ServiceBusReceiveMode.ReceiveAndDelete);

            Func<Task> complete = () => sut.CompleteAsync(AnyMessage());
            Func<Task> abandon = () => sut.AbandonAsync(AnyMessage(), new Dictionary<string, object>());
            Func<Task> deadLetter = () => sut.DeadLetterAsync(AnyMessage(), "reason", "description");

            await complete.Should().NotThrowAsync();
            await abandon.Should().NotThrowAsync();
            await deadLetter.Should().NotThrowAsync();
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
