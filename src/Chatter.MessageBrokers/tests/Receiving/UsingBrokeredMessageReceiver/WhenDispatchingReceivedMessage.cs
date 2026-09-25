#nullable disable

using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingBrokeredMessageReceiver
{
    // INVARIANT: DispatchReceivedMessageAsync throws on an already-signalled token BEFORE its try, so the only way into
    // its catch while the receiver is shutting down is a dispatcher that signals the token and THEN throws. Every fact
    // passes an unsignalled token and lets the dispatcher fake decide whether the shutdown happened mid-dispatch.
    public class WhenDispatchingReceivedMessage : Testing.Core.Context
    {
        private const string _dispatchErrorMessage = "Error dispatching brokered message to handler(s)";

        private readonly RecordingLoggerCreator<BrokeredMessageReceiver<FakeMessage>> _logger;

        public WhenDispatchingReceivedMessage()
            => _logger = New.Common().RecordingLogger<BrokeredMessageReceiver<FakeMessage>>();

        private static MessageBrokerContext BuildContext()
        {
            var converter = new JsonBodyConverter();
            var body = converter.Convert(new FakeMessage { Value = "hello" });
            return new MessageBrokerContext(
                messageId: Guid.NewGuid().ToString(),
                body: body,
                applicationProperties: new Dictionary<string, object>(),
                messageReceiverPath: "test-queue",
                receiverCancellationToken: CancellationToken.None,
                bodyConverter: converter);
        }

        private static Mock<IReceivedMessageDispatcher> DispatcherThatThrows(Exception dispatchFault, CancellationTokenSource shutdownSource)
        {
            var dispatcher = new Mock<IReceivedMessageDispatcher>();
            dispatcher
                .Setup(d => d.DispatchAsync(It.IsAny<FakeMessage>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    shutdownSource?.Cancel();
                    return Task.FromException(dispatchFault);
                });
            return dispatcher;
        }

        private BrokeredMessageReceiver<FakeMessage> CreateSut(Mock<IReceivedMessageDispatcher> dispatcher)
            => new BrokeredMessageReceiver<FakeMessage>(
                infrastructureProvider: new InMemoryMessagingInfrastructureProvider(new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1)),
                messageBrokerOptions: new MessageBrokerOptions(),
                logger: _logger.Creation,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
                recoveryStrategy: new Mock<IRecoveryStrategy>().Object,
                receivedMessageDispatcher: dispatcher.Object);

        private async Task<Exception> DispatchCapturingFaultAsync(Exception dispatchFault, bool shutDownMidDispatch)
        {
            using var receiverTokenSource = new CancellationTokenSource();
            var dispatcher = DispatcherThatThrows(dispatchFault, shutDownMidDispatch ? receiverTokenSource : null);
            var sut = CreateSut(dispatcher);

            var dispatch = () => sut.DispatchReceivedMessageAsync(new FakeMessage { Value = "hello" }, BuildContext(), receiverTokenSource.Token);

            var thrown = await dispatch.Should().ThrowAsync<Exception>();
            return thrown.Which;
        }

        private class FakeMessage : CQRS.IMessage
        {
            public string Value { get; set; }
        }

        [Fact]
        public async Task MustLogAShutdownCancelledDispatchAtDebugInsteadOfError()
        {
            await DispatchCapturingFaultAsync(new OperationCanceledException(), shutDownMidDispatch: true);

            _logger.CountOf(LogLevel.Debug).Should().Be(1);
            _logger.CountOf(LogLevel.Error).Should().Be(0);
        }

        [Fact]
        public async Task MustLogAShutdownObjectDisposedExceptionAtDebugInsteadOfError()
        {
            await DispatchCapturingFaultAsync(new ObjectDisposedException("receiver"), shutDownMidDispatch: true);

            _logger.CountOf(LogLevel.Debug).Should().Be(1);
            _logger.CountOf(LogLevel.Error).Should().Be(0);
        }

        [Fact]
        public async Task MustRethrowTheShutdownCancellationUnchanged()
        {
            var cancellation = new OperationCanceledException();

            var thrown = await DispatchCapturingFaultAsync(cancellation, shutDownMidDispatch: true);

            thrown.Should().BeSameAs(cancellation);
        }

        [Fact]
        public async Task MustStillLogErrorWhenTheCancellationWasNotRequestedByTheReceiverShutdown()
        {
            await DispatchCapturingFaultAsync(new OperationCanceledException(), shutDownMidDispatch: false);

            _logger.CountOf(LogLevel.Error, _dispatchErrorMessage).Should().Be(1);
            _logger.CountOf(LogLevel.Debug).Should().Be(0);
        }

        [Fact]
        public async Task MustStillLogErrorForAnObjectDisposedExceptionWhenTheReceiverIsNotShuttingDown()
        {
            await DispatchCapturingFaultAsync(new ObjectDisposedException("receiver"), shutDownMidDispatch: false);

            _logger.CountOf(LogLevel.Error, _dispatchErrorMessage).Should().Be(1);
            _logger.CountOf(LogLevel.Debug).Should().Be(0);
        }

        [Fact]
        public async Task MustStillLogErrorForANonCancellationFaultWhileTheReceiverIsShuttingDown()
        {
            await DispatchCapturingFaultAsync(new InvalidOperationException("handler boom"), shutDownMidDispatch: true);

            _logger.CountOf(LogLevel.Error, _dispatchErrorMessage).Should().Be(1);
            _logger.CountOf(LogLevel.Debug).Should().Be(0);
        }
    }
}
