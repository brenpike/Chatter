using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingInboundBrokeredMessageFactory
{
    // Pins InboundBrokeredMessageFactory's SessionId -> GroupId stamp: a session message's SessionId is
    // surfaced under the existing core MessageContext.GroupId header, and a non-session message (SessionId
    // absent/empty) leaves GroupId unstamped. SessionId is a received-state field on ServiceBusReceivedMessage,
    // so messages are built via ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: ...) — the same
    // no-reflection received-message construction the shared ServiceBusMessageFactory uses.
    public class WhenCreatingContextForSessionMessage : Testing.Core.Context
    {
        private static InboundBrokeredMessageFactory CreateSut()
        {
            var bodyConverterFactory = new Mock<IBodyConverterFactory>();
            bodyConverterFactory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return new InboundBrokeredMessageFactory(bodyConverterFactory.Object, Mock.Of<ILogger>());
        }

        private static ServiceBusReceivedMessage ReceivedMessage(string sessionId)
            => ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: new BinaryData(new byte[] { 1 }),
                messageId: "message-id",
                contentType: "application/json",
                timeToLive: TimeSpan.FromMinutes(5),
                deliveryCount: 1,
                lockTokenGuid: Guid.NewGuid(),
                enqueuedTime: DateTimeOffset.UtcNow,
                sessionId: sessionId);

        [Fact]
        public void MustStampGroupIdFromSessionIdWhenSessionPresent()
        {
            var sut = CreateSut();
            var message = ReceivedMessage(sessionId: "session-42");

            var context = sut.CreateContext(message, "receiver", CancellationToken.None);

            context.Should().NotBeNull();
            context.BrokeredMessage.MessageContext[MessageContext.GroupId].Should().Be("session-42");
        }

        [Fact]
        public void MustNotStampGroupIdWhenSessionIdAbsent()
        {
            var sut = CreateSut();
            var message = ReceivedMessage(sessionId: null);

            var context = sut.CreateContext(message, "receiver", CancellationToken.None);

            context.Should().NotBeNull();
            context.BrokeredMessage.MessageContext.Should().NotContainKey(MessageContext.GroupId);
        }

        [Fact]
        public void MustNotStampGroupIdWhenSessionIdEmpty()
        {
            var sut = CreateSut();
            var message = ReceivedMessage(sessionId: string.Empty);

            var context = sut.CreateContext(message, "receiver", CancellationToken.None);

            context.Should().NotBeNull();
            context.BrokeredMessage.MessageContext.Should().NotContainKey(MessageContext.GroupId);
        }
    }
}
