using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.MessageBrokers;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests
{
    // Pins the single-pass consumption contract that IRouteBrokeredMessages.Route documents for every downstream that
    // consumes the outbound sequence: it is a lazy iterator whose body RE-RUNS per enumeration (destination
    // resolution, body conversion, message-id generation, trace-context injection, send-span batch counting), so a
    // second pass silently doubles that work and mis-counts the send span. The outbox is compliant today; these tests
    // exist so a Count()-for-capacity hint or a defensive ToList()-then-walk refactor cannot reintroduce the second
    // pass unnoticed.
    public class WhenDispatchSequenceIsEnumerated : Testing.Core.Context
    {
        private readonly DbContextCreator _context;
        private readonly DbContext _dbContext;
        private readonly BrokeredMessageOutbox<DbContext> _sut;
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter;

        public WhenDispatchSequenceIsEnumerated()
        {
            _context = New.MessageBrokers().DbContext();
            _dbContext = _context;

            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(factory => factory.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

            _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            _bodyConverter.SetupGet(converter => converter.ContentType).Returns("application/json");
            _bodyConverter.Setup(converter => converter.Stringify(It.IsAny<byte[]>())).Returns("stringified-body");

            _sut = new BrokeredMessageOutbox<DbContext>(_context, loggerFactory.Object);
        }

        private OutboundBrokeredMessage CreateOutbound(string messageId)
            => new OutboundBrokeredMessage(messageId, new byte[] { 1, 2, 3 }, new Dictionary<string, object>(), "test-destination", _bodyConverter.Object);

        private EnumerationProbeSequence<OutboundBrokeredMessage> Probe(params string[] messageIds)
            => new EnumerationProbeSequence<OutboundBrokeredMessage>(messageIds.Select(CreateOutbound).ToArray(), new List<string>());

        [Fact]
        public async Task MustAskForExactlyOneEnumeratorPerSendToOutbox()
        {
            var probe = Probe("msg-0", "msg-1", "msg-2");

            await _sut.SendToOutbox(probe, null);

            probe.EnumeratorRequestCount.Should().Be(1);
        }

        [Fact]
        public async Task MustYieldEachOutboundMessageExactlyOnceAcrossThePass()
        {
            var probe = Probe("msg-0", "msg-1", "msg-2");

            await _sut.SendToOutbox(probe, null);

            probe.YieldCountsPerPass.Should().Equal(new[] { 3 });
            probe.YieldedCount.Should().Be(probe.ItemCount);
        }

        [Fact]
        public async Task MustPersistExactlyOneOutboxMessagePerOutboundMessage()
        {
            var probe = Probe("msg-0", "msg-1", "msg-2");

            await _sut.SendToOutbox(probe, null);

            var persisted = await _dbContext.Set<OutboxMessage>().ToListAsync();
            persisted.Should().HaveCount(3);
            persisted.Select(message => message.MessageId).Should().BeEquivalentTo(new[] { "msg-0", "msg-1", "msg-2" });
        }

        [Fact]
        public async Task MustAskForExactlyOneEnumeratorWhenSequenceIsEmpty()
        {
            var probe = Probe();

            await _sut.SendToOutbox(probe, null);

            probe.EnumeratorRequestCount.Should().Be(1);
            (await _dbContext.Set<OutboxMessage>().ToListAsync()).Should().BeEmpty();
        }
    }
}
