using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Outbox.UsingInMemoryBrokeredMessageOutbox
{
    public class WhenManagingOutbox : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly RecordingLoggerCreator<InMemoryBrokeredMessageOutbox> _logger;
        private readonly ReliabilityOptions _reliabilityOptions = new ReliabilityOptions();
        private readonly InMemoryBrokeredMessageOutbox _sut;

        public WhenManagingOutbox()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _bodyConverter.Setup(c => c.Stringify(It.IsAny<byte[]>())).Returns("stringified-body");
            _logger = New.Common().RecordingLogger<InMemoryBrokeredMessageOutbox>();
            _sut = new InMemoryBrokeredMessageOutbox(_logger.Creation, _reliabilityOptions);
        }

        private OutboundBrokeredMessage CreateOutbound(string messageId = "message-id", string destination = "destination")
            => new OutboundBrokeredMessage(messageId, new byte[] { 1, 2, 3 }, new Dictionary<string, object>(), destination, _bodyConverter.Object);

        [Fact]
        public void MustThrowArgumentNullExceptionWhenLoggerIsNull()
            => FluentActions.Invoking(() => new InMemoryBrokeredMessageOutbox(null, _reliabilityOptions))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenReliabilityOptionsIsNull()
            => FluentActions.Invoking(() => new InMemoryBrokeredMessageOutbox(_logger.Creation, null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustAddMessageToOutboxAsUnprocessed()
        {
            await _sut.SendToOutbox(CreateOutbound("id-1"), new TransactionContext());
            var unprocessed = await _sut.GetUnprocessedMessagesFromOutbox();
            unprocessed.Should().ContainSingle().Which.MessageId.Should().Be("id-1");
        }

        [Fact]
        public async Task MustMapOutboundFieldsOntoStoredMessage()
        {
            await _sut.SendToOutbox(CreateOutbound("id-1", "queue/path"), new TransactionContext());
            var stored = (await _sut.GetUnprocessedMessagesFromOutbox()).Single();
            stored.Destination.Should().Be("queue/path");
            stored.MessageBody.Should().Be("stringified-body");
            stored.MessageContentType.Should().Be("application/json");
            stored.ProcessedFromOutboxAtUtc.Should().BeNull();
        }

        [Fact]
        public async Task MustThrowInvalidOperationWhenAddingDuplicateMessageId()
        {
            await _sut.SendToOutbox(CreateOutbound("id-1"), new TransactionContext());
            await FluentActions.Invoking(async () => await _sut.SendToOutbox(CreateOutbound("id-1"), new TransactionContext()))
                .Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustAddAllMessagesWhenSendingEnumerable()
        {
            var messages = new[] { CreateOutbound("id-1"), CreateOutbound("id-2") };
            await _sut.SendToOutbox(messages, new TransactionContext());
            (await _sut.GetUnprocessedMessagesFromOutbox()).Should().HaveCount(2);
        }

        [Fact]
        public async Task MustExcludeProcessedMessagesFromUnprocessedQuery()
        {
            await _sut.SendToOutbox(CreateOutbound("id-1"), new TransactionContext());
            var message = (await _sut.GetUnprocessedMessagesFromOutbox()).Single();
            await _sut.UpdateProcessedDate(message);
            (await _sut.GetUnprocessedMessagesFromOutbox()).Should().BeEmpty();
        }

        [Fact]
        public async Task MustStampProcessedDateOnSingleUpdate()
        {
            await _sut.SendToOutbox(CreateOutbound("id-1"), new TransactionContext());
            var message = (await _sut.GetUnprocessedMessagesFromOutbox()).Single();
            await _sut.UpdateProcessedDate(message);
            message.ProcessedFromOutboxAtUtc.Should().NotBeNull();
        }

        [Fact]
        public async Task MustStampProcessedDateOnEnumerableUpdate()
        {
            await _sut.SendToOutbox(new[] { CreateOutbound("id-1"), CreateOutbound("id-2") }, new TransactionContext());
            var messages = (await _sut.GetUnprocessedMessagesFromOutbox()).ToList();
            await _sut.UpdateProcessedDate(messages);
            messages.Should().OnlyContain(m => m.ProcessedFromOutboxAtUtc.HasValue);
        }

        [Fact]
        public async Task MustReturnOnlyMatchingUnprocessedBatch()
        {
            var transactionId = Guid.NewGuid();
            var batchContext = new TransactionContext();
            batchContext.Container.Include<IPersistanceTransaction>(StubTransaction(transactionId));

            await _sut.SendToOutbox(CreateOutbound("id-1"), batchContext);
            await _sut.SendToOutbox(CreateOutbound("id-2"), new TransactionContext());

            var batch = await _sut.GetUnprocessedBatch(transactionId);
            batch.Should().ContainSingle().Which.MessageId.Should().Be("id-1");
        }

        [Fact]
        public async Task MustExcludeProcessedMessagesFromBatchQuery()
        {
            var transactionId = Guid.NewGuid();
            var batchContext = new TransactionContext();
            batchContext.Container.Include<IPersistanceTransaction>(StubTransaction(transactionId));

            await _sut.SendToOutbox(CreateOutbound("id-1"), batchContext);
            var message = (await _sut.GetUnprocessedBatch(transactionId)).Single();
            await _sut.UpdateProcessedDate(message);

            (await _sut.GetUnprocessedBatch(transactionId)).Should().BeEmpty();
        }

        [Fact]
        public async Task MustRetainProcessedMessageWhenMinutesToLiveIsZero()
        {
            // INVARIANT: a non-positive MinutesToLiveInMemory disables expiry cleanup, so the
            // processed message remains in the store (still excluded from the unprocessed query).
            await _sut.SendToOutbox(CreateOutbound("id-1"), new TransactionContext());
            var message = (await _sut.GetUnprocessedMessagesFromOutbox()).Single();
            await _sut.UpdateProcessedDate(message);

            await FluentActions.Invoking(async () => await _sut.SendToOutbox(CreateOutbound("id-1"), new TransactionContext()))
                .Should().ThrowAsync<InvalidOperationException>();
        }

        private static IPersistanceTransaction StubTransaction(Guid transactionId)
        {
            var transaction = new Mock<IPersistanceTransaction>();
            transaction.SetupGet(t => t.TransactionId).Returns(transactionId);
            return transaction.Object;
        }
    }
}
