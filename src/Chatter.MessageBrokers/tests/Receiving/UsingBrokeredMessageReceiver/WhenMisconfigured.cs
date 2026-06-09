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
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingBrokeredMessageReceiver
{
    // INVARIANT: the MaxConcurrentCalls floor guard (value < 1) fails fast and STARTUP-FATALLY. The guard throws in
    // StartReceiverImpl BEFORE IsReceiving flips true, so it is NOT absorbed by StartReceiver's when(this.IsReceiving)
    // post-startup catch and PROPAGATES to the caller — exactly the path .NET relies on to abort host startup loudly
    // rather than leave a silently-stopped receiver running. The message must name the bad value and the receiver path.
    public class WhenMisconfigured : Testing.Core.Context
    {
        private static MessageBrokerOptions BuildOptions(TransactionMode mode = TransactionMode.None)
        {
            var opts = new MessageBrokerOptions();
            opts.TransactionMode = mode;
            return opts;
        }

        private static Mock<IRecoveryStrategy> PassThroughRecovery()
        {
            var mock = new Mock<IRecoveryStrategy>();
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<MessageBrokerContext>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<MessageBrokerContext>>, CancellationToken>((action, _) => action());
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<bool>>, CancellationToken>((action, _) => action());
            mock.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                .Returns<Func<Task<int>>, CancellationToken>((action, _) => action());
            return mock;
        }

        private static ReceiverOptions BuildReceiverOptions(int maxConcurrentCalls)
            => new ReceiverOptions
            {
                InfrastructureType = InMemoryMessagingInfrastructureProvider.InfrastructureType,
                MessageReceiverPath = "test-queue",
                SendingPath = "test-queue",
                ErrorQueuePath = "error-queue",
                DeadLetterQueuePath = "deadletter-queue",
                TransactionMode = TransactionMode.None,
                MaxReceiveAttempts = 10,
                MaxConcurrentCalls = maxConcurrentCalls,
            };

        private BrokeredMessageReceiver<FakeMessage> CreateSut(InMemoryMessagingInfrastructureReceiver infraReceiver)
        {
            var provider = new InMemoryMessagingInfrastructureProvider(infraReceiver);

            return new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: provider,
                messageBrokerOptions: BuildOptions(),
                logger: NullLogger<BrokeredMessageReceiver<FakeMessage>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
                recoveryStrategy: PassThroughRecovery().Object,
                receivedMessageDispatcher: new Mock<IReceivedMessageDispatcher>().Object);
        }

        private class FakeMessage : CQRS.IMessage
        {
            public string Value { get; set; }
        }

        // ------------------------------------------------------------------ MaxConcurrentCalls floor guard is startup-fatal and propagates

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public async Task MustThrowStartupFatalWhenMaxConcurrentCallsBelowOne(int maxConcurrentCalls)
        {
            var infraReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 0);

            var sut = CreateSut(infraReceiver);

            using var cts = new CancellationTokenSource();

            // The guard must PROPAGATE to the caller (startup-fatal), not be swallowed by the when(this.IsReceiving)
            // post-startup catch — IsReceiving is still false when the guard fires.
            Func<Task> act = () => sut.StartReceiver(BuildReceiverOptions(maxConcurrentCalls), cts.Token);

            var assertion = await act.Should().ThrowAsync<InvalidOperationException>(
                "a MaxConcurrentCalls value below 1 must fail fast and propagate as a startup-fatal exception");

            // The message must name the bad value and the receiver path so the misconfiguration is diagnosable.
            assertion.Which.Message.Should().Contain(maxConcurrentCalls.ToString(),
                "the message must name the bad MaxConcurrentCalls value");
            assertion.Which.Message.Should().Contain("test-queue",
                "the message must name the receiver path");

            sut.IsReceiving.Should().BeFalse("the guard fires before go-live, so the receiver never started");
            infraReceiver.CallLog.Should().NotContain(ReceiverCall.Receive,
                "no message may be received when startup fails fast on the floor guard");
        }
    }
}
