using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Moq;
using System;
using System.Collections;
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
    // Dispatch creates a sender for the destination and sends the mapped ServiceBusMessage. Also pinned:
    // the batch overload walks its sequence exactly once, sends one message per item, and accepts a
    // sequence that can only be enumerated once.
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

        // Hands out an enumerator once and throws on any later request, so a caller that walks the sequence
        // twice fails outright instead of silently re-running the producer. The shared EnumerationProbeSequence
        // records re-enumeration rather than refusing it, and the refusing SinglePassEventSequence fixture lives
        // in the Message Brokers test project, which this project does not reference.
        private class OneShotSequence<TItem> : IEnumerable<TItem>
        {
            private readonly TItem[] _items;
            private bool _enumeratorHandedOut;

            public OneShotSequence(params TItem[] items) => _items = items;

            public IEnumerator<TItem> GetEnumerator()
            {
                if (_enumeratorHandedOut)
                {
                    throw new InvalidOperationException("This sequence can only be enumerated once.");
                }

                _enumeratorHandedOut = true;

                return ((IEnumerable<TItem>)_items).GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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

        [Fact]
        public async Task MustEnumerateDispatchSequenceExactlyOnce()
        {
            var factory = new RecordingSenderFactory();
            var sut = new ServiceBusMessageSender(factory);
            var sequence = new EnumerationProbeSequence<OutboundBrokeredMessage>(CreateBrokeredMessage(), CreateBrokeredMessage(), CreateBrokeredMessage());

            await sut.Dispatch(sequence, new TransactionContext("receiver", TransactionMode.None));

            sequence.EnumeratorRequestCount.Should().Be(1);
            sequence.YieldCountsPerPass.Should().Equal(new[] { 3 });
        }

        [Fact]
        public async Task MustSendOneMessagePerItemInDispatchSequence()
        {
            var factory = new RecordingSenderFactory();
            var sut = new ServiceBusMessageSender(factory);
            var sequence = new EnumerationProbeSequence<OutboundBrokeredMessage>(CreateBrokeredMessage(), CreateBrokeredMessage(), CreateBrokeredMessage());

            await sut.Dispatch(sequence, new TransactionContext("receiver", TransactionMode.None));

            factory.Sent.Should().HaveCount(3);
            factory.Destinations.Should().HaveCount(3).And.OnlyContain(destination => destination == "destination");
        }

        [Fact]
        public async Task MustDispatchSequenceThatAllowsOnlyOneEnumeration()
        {
            var factory = new RecordingSenderFactory();
            var sut = new ServiceBusMessageSender(factory);
            var sequence = new OneShotSequence<OutboundBrokeredMessage>(CreateBrokeredMessage(), CreateBrokeredMessage());

            Func<Task> act = () => sut.Dispatch(sequence, new TransactionContext("receiver", TransactionMode.None));

            await act.Should().NotThrowAsync();
            factory.Sent.Should().HaveCount(2);
        }

        // NOTE (characterization finding): ServiceBusMessageSender.Dispatch contains a
        // null/empty-Destination ArgumentNullException guard, but it is unreachable through the
        // public OutboundBrokeredMessage type: that type's own ctor already rejects a null/empty
        // destination with ArgumentException before a sender can ever see one. The sender's
        // destination guard is therefore shadowed and cannot be pinned behavior-preservingly via
        // the real message type, so no test asserts it. Preserve the guard as-is during the refactor.
    }
}
