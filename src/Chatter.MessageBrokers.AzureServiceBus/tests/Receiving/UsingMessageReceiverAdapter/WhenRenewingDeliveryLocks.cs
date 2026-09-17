using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using SdkServiceBusReceiver = Azure.Messaging.ServiceBus.ServiceBusReceiver;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingMessageReceiverAdapter
{
    // Pins the NON-SESSION adapter's message-lock renewal lifetime. Without it a handler that outlasts the
    // entity's lock duration loses the lock, the delivery is redelivered while the handler is still running,
    // and each redelivery increments DeliveryCount until a healthy, successfully-handled message reaches
    // MaxReceiveAttempts and is dead-lettered.
    //
    // The Azure SDK receiver is mockable (Azure.Messaging.ServiceBus exposes protected constructors and virtual
    // members for exactly this), so renewal is observed against the SAME receiver instance that delivered the
    // message rather than against a live namespace.
    public class WhenRenewingDeliveryLocks : Testing.Core.Context
    {
        private const string _receiverPath = "message-queue";
        private const string CompleteSettlement = "complete";
        private const string AbandonSettlement = "abandon";
        private const string DeadLetterSettlement = "deadletter";
        private static readonly TimeSpan _renewalWindow = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task ItRenewsAPeekLockDeliveryWhileTheWorkerHoldsIt()
        {
            var sdkReceiver = CreateSdkReceiver(ServiceBusMessageFactory.ReceivedMessage());
            var sut = CreateSut(sdkReceiver);

            await sut.ReceiveAsync(CancellationToken.None);

            sut.ActiveRenewalCount.Should().Be(1, "a PeekLock delivery holds a lock the handler can outlast, so its renewal starts with the delivery");

            await sut.CloseAsync();
        }

        [Fact]
        public async Task ItStartsNoRenewalForAReceiveAndDeleteDelivery()
        {
            var sdkReceiver = CreateSdkReceiver(ServiceBusMessageFactory.ReceivedMessage());
            var sut = CreateSut(sdkReceiver, ServiceBusReceiveMode.ReceiveAndDelete);

            await sut.ReceiveAsync(CancellationToken.None);

            sut.ActiveRenewalCount.Should().Be(0, "ReceiveAndDelete removes the delivery on receipt, so there is no lock to renew");
        }

        [Fact]
        public async Task ItStartsNoRenewalWhenTheReceiveYieldsNoMessage()
        {
            var sdkReceiver = CreateSdkReceiver(deliverable: null);
            var sut = CreateSut(sdkReceiver);

            await sut.ReceiveAsync(CancellationToken.None);

            sut.ActiveRenewalCount.Should().Be(0, "an empty receive delivered no lock to renew");
        }

        [Fact]
        public async Task ItRenewsTheDeliveredLockOnTheReceiverThatDeliveredIt()
        {
            var delivery = ServiceBusMessageFactory.ReceivedMessage();
            var sdkReceiver = CreateSdkReceiver(delivery);
            var renewed = new TaskCompletionSource<ServiceBusReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            sdkReceiver.Setup(receiver => receiver.RenewMessageLockAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
                       .Callback<ServiceBusReceivedMessage, CancellationToken>((message, _) => renewed.TrySetResult(message))
                       .Returns(Task.CompletedTask);
            var sut = CreateSut(sdkReceiver);

            await sut.ReceiveAsync(CancellationToken.None);

            (await CompletedWithin(renewed.Task, _waitTimeout)).Should().BeTrue("the held lock is renewed before it expires, which is the whole point of the loop");
            (await renewed.Task).Should().BeSameAs(delivery, "renewal targets the delivery the worker is still handling, by the message object the SDK settles against");

            await sut.CloseAsync();
        }

        // ORDER IS LOAD-BEARING, and it mirrors the session adapter's stated invariant: renewal is ended BEFORE
        // the settlement reaches the broker, so no renewal call races a delivery that is already settled.
        [Theory]
        [InlineData(CompleteSettlement)]
        [InlineData(AbandonSettlement)]
        [InlineData(DeadLetterSettlement)]
        public async Task ItStopsRenewingBeforeTheSettlementReachesTheBroker(string settlement)
        {
            var delivery = ServiceBusMessageFactory.ReceivedMessage();
            var sdkReceiver = CreateSdkReceiver(delivery);
            AzureSdkMessageReceiverAdapter sut = null;
            var renewalsWhenSettling = -1;
            ObserveRenewalsWhenSettling(sdkReceiver, settlement, () => renewalsWhenSettling = sut.ActiveRenewalCount);
            sut = CreateSut(sdkReceiver);
            await sut.ReceiveAsync(CancellationToken.None);

            await SettleAsync(sut, settlement, delivery);

            renewalsWhenSettling.Should().Be(0, "every settle path funnels through one stop, so a settled delivery is never still renewing when the broker hears about it");
            sut.ActiveRenewalCount.Should().Be(0, "a settled delivery holds no lock to renew");
        }

        // THE LOAD-BEARING CASE: a handler that throws reaches no settle member at all — the core's own recovery
        // ladder can itself fail — so the release signal raised from the worker's finally is the ONLY guaranteed
        // stop. This is why DeliveryReleased sits on the non-session port.
        [Fact]
        public async Task ItStopsRenewingADeliveryReleasedWithoutBeingSettled()
        {
            var delivery = ServiceBusMessageFactory.ReceivedMessage();
            var sdkReceiver = CreateSdkReceiver(delivery);
            var sut = CreateSut(sdkReceiver);
            await sut.ReceiveAsync(CancellationToken.None);

            sut.DeliveryReleased(delivery);

            sut.ActiveRenewalCount.Should().Be(0, "a delivery the worker is finished with stops renewing even when nothing settled it");
            sdkReceiver.Verify(receiver => receiver.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public void ItIgnoresAReleaseForADeliveryItNeverHandled()
        {
            var sut = CreateSut(CreateSdkReceiver(deliverable: null));

            Action release = () => sut.DeliveryReleased(ServiceBusMessageFactory.ReceivedMessage(messageId: "never-delivered"));

            release.Should().NotThrow("a receiver rebuilt after an ObjectDisposedException routes the discarded adapter's in-flight release to an adapter that never saw that delivery");
        }

        [Fact]
        public async Task ItStopsEveryRenewalBeforeClosingTheReceiver()
        {
            var sdkReceiver = CreateSdkReceiver(ServiceBusMessageFactory.ReceivedMessage());
            AzureSdkMessageReceiverAdapter sut = null;
            var renewalsWhenClosing = -1;
            sdkReceiver.Setup(receiver => receiver.CloseAsync(It.IsAny<CancellationToken>()))
                       .Callback(() => renewalsWhenClosing = sut.ActiveRenewalCount)
                       .Returns(Task.CompletedTask);
            sut = CreateSut(sdkReceiver);
            await sut.ReceiveAsync(CancellationToken.None);

            await sut.CloseAsync();

            renewalsWhenClosing.Should().Be(0, "a renewal still running against a closing receiver would outlive the receiver that owns it");
        }

        private static async Task<bool> CompletedWithin(Task pending, TimeSpan timeout)
            => await Task.WhenAny(pending, Task.Delay(timeout)).ConfigureAwait(false) == pending;

        private static Task<ServiceBusSettlementOutcome> SettleAsync(AzureSdkMessageReceiverAdapter sut, string settlement, ServiceBusReceivedMessage delivery)
            => settlement switch
            {
                CompleteSettlement => sut.CompleteAsync(delivery),
                AbandonSettlement => sut.AbandonAsync(delivery, new Dictionary<string, object>()),
                DeadLetterSettlement => sut.DeadLetterAsync(delivery, "reason", "description"),
                _ => throw new ArgumentOutOfRangeException(nameof(settlement), settlement, "Unknown settlement."),
            };

        // Runs <paramref name="observe"/> at the instant the settlement reaches the SDK receiver, which is what
        // makes the stop-before-settle ordering observable rather than merely the end state.
        private static void ObserveRenewalsWhenSettling(Mock<SdkServiceBusReceiver> sdkReceiver, string settlement, Action observe)
        {
            switch (settlement)
            {
                case CompleteSettlement:
                    sdkReceiver.Setup(receiver => receiver.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
                               .Callback(observe)
                               .Returns(Task.CompletedTask);
                    return;
                case AbandonSettlement:
                    sdkReceiver.Setup(receiver => receiver.AbandonMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<IDictionary<string, object>>(), It.IsAny<CancellationToken>()))
                               .Callback(observe)
                               .Returns(Task.CompletedTask);
                    return;
                case DeadLetterSettlement:
                    sdkReceiver.Setup(receiver => receiver.DeadLetterMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                               .Callback(observe)
                               .Returns(Task.CompletedTask);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(settlement), settlement, "Unknown settlement.");
            }
        }

        private static Mock<SdkServiceBusReceiver> CreateSdkReceiver(ServiceBusReceivedMessage deliverable)
        {
            var sdkReceiver = new Mock<SdkServiceBusReceiver>();
            sdkReceiver.Setup(receiver => receiver.ReceiveMessageAsync(It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(deliverable);
            sdkReceiver.Setup(receiver => receiver.CloseAsync(It.IsAny<CancellationToken>()))
                       .Returns(Task.CompletedTask);
            return sdkReceiver;
        }

        private static AzureSdkMessageReceiverAdapter CreateSut(Mock<SdkServiceBusReceiver> sdkReceiver,
                                                                ServiceBusReceiveMode receiveMode = ServiceBusReceiveMode.PeekLock,
                                                                TimeSpan? maxMessageLockRenewalDuration = null)
        {
            var client = new Mock<ServiceBusClient>();
            client.Setup(c => c.CreateReceiver(_receiverPath, It.IsAny<ServiceBusReceiverOptions>()))
                  .Returns(sdkReceiver.Object);

            return new AzureSdkMessageReceiverAdapter(client.Object,
                                                      _receiverPath,
                                                      receiveMode,
                                                      prefetchCount: 0,
                                                      maxMessageLockRenewalDuration ?? _renewalWindow,
                                                      Mock.Of<ILogger>());
        }
    }
}
