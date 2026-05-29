using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    public class WhenSendingToOutbox : Testing.Core.Context
    {
        private DbContextCreator _context;
        private readonly DbContext _dbContext;
        private readonly BrokeredMessageOutbox<DbContext> _sut;
        private readonly Mock<ILoggerFactory> _loggerFactory;
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter;

        public WhenSendingToOutbox()
        {
            _context = New.MessageBrokers().DbContext();
            _dbContext = _context;
            _loggerFactory = new Mock<ILoggerFactory>();
            _loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _bodyConverter.Setup(c => c.Stringify(It.IsAny<byte[]>())).Returns("stringified-body");
            _sut = new BrokeredMessageOutbox<DbContext>(_context, _loggerFactory.Object);
        }

        private OutboundBrokeredMessage CreateOutbound(string messageId = "msg-1", string destination = "test-destination")
            => new OutboundBrokeredMessage(messageId, new byte[] { 1, 2, 3 }, new Dictionary<string, object>(), destination, _bodyConverter.Object);

        [Fact]
        public async Task MustPersistOneOutboxMessagePerOutboundMessage()
        {
            var outbound = CreateOutbound();

            await _sut.SendToOutbox(new[] { outbound }, null);

            var persisted = await _dbContext.Set<OutboxMessage>().ToListAsync();
            persisted.Should().HaveCount(1);
        }

        [Fact]
        public async Task MustCopyMessageIdDestinationAndContentTypeFromOutboundMessage()
        {
            var outbound = CreateOutbound();

            await _sut.SendToOutbox(new[] { outbound }, null);

            var persisted = (await _dbContext.Set<OutboxMessage>().ToListAsync()).Single();
            persisted.MessageId.Should().Be(outbound.MessageId);
            persisted.Destination.Should().Be(outbound.Destination);
            persisted.MessageContentType.Should().Be(outbound.ContentType);
        }

        [Fact]
        public async Task MustStoreNewtonsoftSerializedMessageContextOfOutboundMessage()
        {
            var outbound = CreateOutbound();

            await _sut.SendToOutbox(new[] { outbound }, null);

            var persisted = (await _dbContext.Set<OutboxMessage>().ToListAsync()).Single();
            persisted.MessageContext.Should().Be(JsonConvert.SerializeObject(outbound.MessageContext));
        }

        [Fact]
        public async Task MustStoreMessageBodyFromStringify()
        {
            var outbound = CreateOutbound();

            await _sut.SendToOutbox(new[] { outbound }, null);

            var persisted = (await _dbContext.Set<OutboxMessage>().ToListAsync()).Single();
            persisted.MessageBody.Should().Be(outbound.Stringify());
        }

        [Fact]
        public async Task MustStampSentToOutboxAtUtcAndLeaveProcessedDateNull()
        {
            var outbound = CreateOutbound();

            await _sut.SendToOutbox(new[] { outbound }, null);

            var persisted = (await _dbContext.Set<OutboxMessage>().ToListAsync()).Single();
            persisted.SentToOutboxAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            persisted.ProcessedFromOutboxAtUtc.Should().BeNull();
        }

        [Fact]
        public async Task MustSetBatchIdToGuidEmptyWhenTransactionContextIsNull()
        {
            var outbound = CreateOutbound();

            await _sut.SendToOutbox(new[] { outbound }, null);

            var persisted = (await _dbContext.Set<OutboxMessage>().ToListAsync()).Single();
            persisted.BatchId.Should().Be(Guid.Empty);
        }

        [Fact]
        public async Task MustPersistNothingButStillSaveWhenEnumerableIsEmpty()
        {
            await _sut.SendToOutbox(Enumerable.Empty<OutboundBrokeredMessage>(), null);

            var persisted = await _dbContext.Set<OutboxMessage>().ToListAsync();
            persisted.Should().BeEmpty();
        }

        [Fact]
        public void MustThrowWhenContextIsNull()
        {
            Action act = () => new BrokeredMessageOutbox<DbContext>(null, _loggerFactory.Object);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenLoggerFactoryIsNull()
        {
            Action act = () => new BrokeredMessageOutbox<DbContext>(_context, null);

            act.Should().Throw<ArgumentNullException>();
        }
    }
}
