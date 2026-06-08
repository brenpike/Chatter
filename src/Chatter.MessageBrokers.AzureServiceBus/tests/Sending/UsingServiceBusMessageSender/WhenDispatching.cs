using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Sending.UsingServiceBusMessageSender
{
    // Characterization tests over ServiceBusMessageSender driven through the internal
    // IServiceBusMessageSenderFactory seam. The factory hands back a Moq'd Azure.Messaging.ServiceBus
    // ServiceBusSender (its SendMessageAsync member is virtual), so dispatch is exercised without a live
    // client. Pinned: the constructor null guard, the single-message Dispatch null guard, and that
    // Dispatch creates a sender for the destination and sends the mapped ServiceBusMessage.
    public class WhenDispatching : Testing.Core.Context
    {
        private readonly byte[] _body = new byte[] { 1, 2, 3 };
        private readonly JsonBodyConverter _converter = new JsonBodyConverter();

        private OutboundBrokeredMessage CreateBrokeredMessage()
            => new OutboundBrokeredMessage("message-id", _body, new Dictionary<string, object>(), "destination", _converter);

        // Records each requested destination and returns a Moq'd ServiceBusSender whose SendMessageAsync
        // completes synchronously, so dispatch can be driven without opening a live connection.
        private class RecordingSenderFactory : IServiceBusMessageSenderFactory
        {
            public List<string> Destinations { get; } = new List<string>();
            public List<ServiceBusMessage> Sent { get; } = new List<ServiceBusMessage>();

            public ServiceBusSender Create(string destinationEntityPath)
            {
                Destinations.Add(destinationEntityPath);
                var sender = new Mock<ServiceBusSender>();
                sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
                      .Returns((ServiceBusMessage message, CancellationToken _) =>
                      {
                          Sent.Add(message);
                          return Task.CompletedTask;
                      });
                return sender.Object;
            }
        }

        [Fact]
        public void MustThrowWhenSenderFactoryNull()
        {
            Action act = () => new ServiceBusMessageSender(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenBrokeredMessageNull()
        {
            var sut = new ServiceBusMessageSender(new RecordingSenderFactory());
            Action act = () => sut.Dispatch((OutboundBrokeredMessage)null, null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public async Task MustCreateSenderForDestination()
        {
            var factory = new RecordingSenderFactory();
            var sut = new ServiceBusMessageSender(factory);

            await sut.Dispatch(CreateBrokeredMessage(), new TransactionContext("receiver", TransactionMode.None));

            factory.Destinations.Should().ContainSingle().Which.Should().Be("destination");
        }

        [Fact]
        public async Task MustSendMappedMessage()
        {
            var factory = new RecordingSenderFactory();
            var sut = new ServiceBusMessageSender(factory);

            await sut.Dispatch(CreateBrokeredMessage(), new TransactionContext("receiver", TransactionMode.None));

            factory.Sent.Should().ContainSingle().Which.MessageId.Should().Be("message-id");
        }

        // NOTE (characterization finding): ServiceBusMessageSender.Dispatch contains a
        // null/empty-Destination ArgumentNullException guard, but it is unreachable through the
        // public OutboundBrokeredMessage type: that type's own ctor already rejects a null/empty
        // destination with ArgumentException before a sender can ever see one. The sender's
        // destination guard is therefore shadowed and cannot be pinned behavior-preservingly via
        // the real message type, so no test asserts it. Preserve the guard as-is during the refactor.
    }
}
