#nullable disable

using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingBrokeredMessageReceiver
{
    // INVARIANT: drives the generic error ladder end to end with an infrastructure that INHERITS the default
    // delivery-count probe and a delivery whose Receive Attempts is unreadable. The ladder awaits that probe before
    // it settles anything, so a probe that throws leaves the delivery UNSETTLED — neither negatively acknowledged
    // nor deadlettered — and the infrastructure redelivers it forever while the receiver keeps reporting healthy.
    // Each case here asserts the delivery was DEADLETTERED, which is the only observable escape available to a
    // member the interface gives no logger.
    public class WhenDeliveryCountHeaderIsUnusable : Testing.Core.Context
    {
        private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);

        public static IEnumerable<object[]> UnusableReceiveAttempts()
        {
            yield return new object[] { false, null, "an absent Receive Attempts" };
            yield return new object[] { true, null, "a null Receive Attempts" };
            yield return new object[] { true, "3", "a string Receive Attempts" };
            yield return new object[] { true, 3L, "a long Receive Attempts" };
            yield return new object[] { true, new byte[] { 0, 0, 0, 3 }, "a byte[] Receive Attempts" };
        }

        [Theory]
        [MemberData(nameof(UnusableReceiveAttempts))]
        public async Task MustDeadletterTheDeliveryWhenTheHandlerThrows(bool keyIsPresent, object receiveAttempts, string because)
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            var probe = new InheritsTheDefaultDeliveryCountProbe(infraReceiver);
            infraReceiver.Enqueue(BuildContext(keyIsPresent, receiveAttempts));

            var sut = CreateSut(probe, ThrowingDispatcher());

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(Watchdog);
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Deadletter), watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Should().Contain(ReceiverCall.Deadletter,
                $"the ladder must settle a delivery it cannot count, and {because} leaves it uncountable");
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Ack);
        }

        [Fact]
        public async Task MustNackTheDeliveryWhenReceiveAttemptsIsAnIntBelowMax()
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1);
            var probe = new InheritsTheDefaultDeliveryCountProbe(infraReceiver);
            infraReceiver.Enqueue(BuildContext(keyIsPresent: true, receiveAttempts: 1));

            var sut = CreateSut(probe, ThrowingDispatcher());

            using var cts = new CancellationTokenSource();
            var loop = Task.Run(() => sut.StartReceiver(BuildReceiverOptions(), cts.Token));

            using var watchdog = new CancellationTokenSource(Watchdog);
            await WaitUntilAsync(() => infraReceiver.CallLog.Contains(ReceiverCall.Nack), watchdog.Token);

            cts.Cancel();
            await loop;

            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Deadletter,
                "a readable Receive Attempts below the maximum must still take the redelivery branch, so the sentinel never swallows a countable delivery");
        }

        private static Mock<IReceivedMessageDispatcher> ThrowingDispatcher()
        {
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("handler boom"));
            return dispatcher;
        }

        private static BrokeredMessageReceiver<FakeMessage> CreateSut(IMessagingInfrastructureReceiver infraReceiver, Mock<IReceivedMessageDispatcher> dispatcher)
        {
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);

            return new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: new MessageBrokerOptions { TransactionMode = TransactionMode.None },
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
                recoveryStrategy: PassThroughRecovery().Object,
                receivedMessageDispatcher: dispatcher.Object);
        }

        private static Mock<IRecoveryStrategy> PassThroughRecovery()
        {
            var mock = new Mock<IRecoveryStrategy>();
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<MessageBrokerContext>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<MessageBrokerContext>>, CancellationToken>((action, _) => action());
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<bool>>, CancellationToken>((action, _) => action());
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<SettlementResult>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<SettlementResult>>, CancellationToken>((action, _) => action());
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<int>>, CancellationToken>((action, _) => action());
            return mock;
        }

        private static ReceiverOptions BuildReceiverOptions()
            => new ReceiverOptions
            {
                InfrastructureType = InMemoryMessagingInfrastructureProvider.InfrastructureType,
                MessageReceiverPath = "test-queue",
                SendingPath = "test-queue",
                ErrorQueuePath = "error-queue",
                DeadLetterQueuePath = "deadletter-queue",
                TransactionMode = TransactionMode.None,
                MaxReceiveAttempts = 10,
                MaxConcurrentCalls = 1,
            };

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
                body: converter.Convert(new FakeMessage { Value = "hello" }),
                applicationProperties: applicationProperties,
                messageReceiverPath: "test-queue",
                receiverCancellationToken: CancellationToken.None,
                bodyConverter: converter);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken watchdog)
        {
            while (!predicate())
            {
                watchdog.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        public class FakeMessage : CQRS.IMessage
        {
            public string Value { get; set; }
        }
    }
}
