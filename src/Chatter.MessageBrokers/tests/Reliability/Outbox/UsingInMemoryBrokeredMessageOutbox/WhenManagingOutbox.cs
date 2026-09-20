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
        // IPollableOutboxStore.RecordDispatchAttempt is a default interface implementation, so it is reachable
        // ONLY through the interface - which is also how the poller reaches it, since it resolves the single
        // outbox and casts. Calling it through this handle is what lets a store that inherits the no-op default
        // redden these facts instead of failing to compile.
        private readonly IPollableOutboxStore _pollableStore;

        public WhenManagingOutbox()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _bodyConverter.Setup(c => c.Stringify(It.IsAny<byte[]>())).Returns("stringified-body");
            _logger = New.Common().RecordingLogger<InMemoryBrokeredMessageOutbox>();
            _sut = new InMemoryBrokeredMessageOutbox(_logger.Creation, _reliabilityOptions);
            _pollableStore = _sut;
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
        public async Task MustStoreNewtonsoftSerializedMessageContextWireString()
        {
            // This literal pins the CURRENT Newtonsoft wire form of the stored MessageContext.
            // The OutboundBrokeredMessage ctor injects two headers in this order: ContentType
            // (from the body converter) then CorrelationId (pinned via WithCorrelationId so the
            // wire form is deterministic). MUST be updated when the Phase-2 STJ port intentionally
            // changes the serialized form.
            var outbound = CreateOutbound("id-1", "queue/path").WithCorrelationId("fixed-correlation-id");

            await _sut.SendToOutbox(outbound, new TransactionContext());

            var stored = (await _sut.GetUnprocessedMessagesFromOutbox()).Single();
            var expectedWire = $"{{\"{MessageContext.ContentType}\":\"application/json\",\"{MessageContext.CorrelationId}\":\"fixed-correlation-id\"}}";
            stored.MessageContext.Should().Be(expectedWire);
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

        [Fact]
        public async Task MustRetainProcessedMessageWithoutFaultingWhenMinutesToLiveOutrunsTheDateRange()
        {
            // INVARIANT: the scan compares ELAPSED minutes against the ttl instead of adding the ttl to the
            // processed timestamp, so a ttl no elapsed time can ever reach costs nothing and expires nothing.
            // Adding it threw an ArgumentOutOfRangeException out of UpdateProcessedDate, which OutboxProcessor
            // calls inside its unit of work BEFORE dispatching and catches around the whole block, so every
            // message was stamped processed, logged and then never dispatched and never retried.
            _reliabilityOptions.MinutesToLiveInMemory = 1e300;
            await _sut.SendToOutbox(CreateOutbound("id-1"), new TransactionContext());
            var message = (await _sut.GetUnprocessedMessagesFromOutbox()).Single();

            await FluentActions.Invoking(async () => await _sut.UpdateProcessedDate(message))
                .Should().NotThrowAsync();

            await FluentActions.Invoking(async () => await _sut.SendToOutbox(CreateOutbound("id-1"), new TransactionContext()))
                .Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustReturnNoMoreMessagesThanTheOutboxPollBatchSize()
        {
            await SeedOutboxAsync(("id-1", 4), ("id-2", 0), ("id-3", 3), ("id-4", 1), ("id-5", 2));
            _reliabilityOptions.OutboxPollBatchSize = 2;

            var polled = await _sut.GetUnprocessedMessagesFromOutbox();

            polled.Select(m => m.MessageId).Should().Equal("id-2", "id-4");
        }

        [Fact]
        public async Task MustReturnOldestSentToOutboxMessagesFirst()
        {
            await SeedOutboxAsync(("id-1", 4), ("id-2", 0), ("id-3", 3), ("id-4", 1), ("id-5", 2));

            var polled = await _sut.GetUnprocessedMessagesFromOutbox();

            polled.Select(m => m.MessageId).Should().Equal("id-2", "id-4", "id-5", "id-3", "id-1");
        }

        [Fact]
        public async Task MustExcludeAMessageWhoseNextAttemptHasNotArrived()
        {
            // INVARIANT: an unprocessed message is polled only once it is DUE, and a null NextAttemptAtUtc - the
            // value a staged message carries - is due now. Both halves are read here: id-1 is held back by an
            // instant still ahead, id-2 is taken with no instant at all.
            var stored = await SeedOutboxAsync(("id-1", 0), ("id-2", 1));
            stored["id-1"].NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(5);

            var polled = await _sut.GetUnprocessedMessagesFromOutbox();

            polled.Select(m => m.MessageId).Should().Equal("id-2");
        }

        [Fact]
        public async Task MustIncludeAMessageWhoseNextAttemptHasPassed()
        {
            // INVARIANT: the due clause HOLDS a message back rather than retiring it - once the instant has passed
            // the message is polled again, which is what makes the backoff a deferral and not a drop.
            var stored = await SeedOutboxAsync(("id-1", 0));
            stored["id-1"].NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(-5);

            var polled = await _sut.GetUnprocessedMessagesFromOutbox();

            polled.Select(m => m.MessageId).Should().Equal("id-1");
        }

        [Fact]
        public async Task MustSpendNoBatchSlotOnAMessageThatIsNotDue()
        {
            // INVARIANT: the due clause is applied BEFORE the batch cap. Gating the rows the cap already took
            // would shrink the batch instead of filling it from behind, and a batch's worth of held-back messages
            // would then return nothing at all - the very starvation the clause exists to end.
            var stored = await SeedOutboxAsync(("id-1", 0), ("id-2", 1), ("id-3", 2));
            stored["id-1"].NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(5);
            _reliabilityOptions.OutboxPollBatchSize = 2;

            var polled = await _sut.GetUnprocessedMessagesFromOutbox();

            polled.Select(m => m.MessageId).Should().Equal("id-2", "id-3");
        }

        [Fact]
        public async Task MustGiveTheBatchSlotOfAFailingMessageToTheNextMessage()
        {
            // INVARIANT: the ELIMINATED CLASS. A message whose dispatch keeps failing cannot hold a selection slot
            // in the default in-process configuration, because occupancy is a function of a due instant the failure
            // path advances rather than of failure itself. At a batch size of one, id-1 owns the whole batch until
            // its attempt is recorded; the next poll belongs to id-2.
            var stored = await SeedOutboxAsync(("id-1", 0), ("id-2", 1));
            _reliabilityOptions.OutboxPollBatchSize = 1;

            var firstPoll = await _sut.GetUnprocessedMessagesFromOutbox();
            firstPoll.Select(m => m.MessageId).Should().Equal("id-1");

            await _pollableStore.RecordDispatchAttempt(stored["id-1"],
                                                       DateTime.UtcNow.Add(_reliabilityOptions.CalculateDispatchBackoff(1)));

            var secondPoll = await _sut.GetUnprocessedMessagesFromOutbox();

            secondPoll.Select(m => m.MessageId).Should().Equal("id-2");
        }

        [Fact]
        public async Task MustExcludeAMessageThatHasSpentTheConfiguredAttemptCeiling()
        {
            // INVARIANT: the ceiling compares attempts already SPENT against the configured most, so a message
            // that has spent all of them is no longer polled even though it is due. Its two recorded attempts are
            // both stamped due-now, so the due clause cannot be what withholds it.
            var stored = await SeedOutboxAsync(("id-1", 0), ("id-2", 1));
            _reliabilityOptions.OutboxMaxDispatchAttempts = 2;
            await _pollableStore.RecordDispatchAttempt(stored["id-1"], DateTime.UtcNow.AddMinutes(-5));
            await _pollableStore.RecordDispatchAttempt(stored["id-1"], DateTime.UtcNow.AddMinutes(-5));

            var polled = await _sut.GetUnprocessedMessagesFromOutbox();

            polled.Select(m => m.MessageId).Should().Equal("id-2");
        }

        [Fact]
        public async Task MustIncludeAMessageBelowTheConfiguredAttemptCeiling()
        {
            // INVARIANT: the ceiling is the count of attempts a message may be GIVEN, not the count it may
            // survive, so one spent attempt out of two leaves the second one owed.
            var stored = await SeedOutboxAsync(("id-1", 0));
            _reliabilityOptions.OutboxMaxDispatchAttempts = 2;
            await _pollableStore.RecordDispatchAttempt(stored["id-1"], DateTime.UtcNow.AddMinutes(-5));

            var polled = await _sut.GetUnprocessedMessagesFromOutbox();

            polled.Select(m => m.MessageId).Should().Equal("id-1");
        }

        [Fact]
        public async Task MustApplyNoAttemptCeilingWhenOutboxMaxDispatchAttemptsIsAbsent()
        {
            // INVARIANT: an absent OutboxMaxDispatchAttempts - the default - is NO ceiling at all rather than a
            // ceiling of some fallback number, so the shipped configuration keeps re-attempting a message for
            // good, which is what a host already running this package does today.
            var stored = await SeedOutboxAsync(("id-1", 0));
            for (var attemptsSpent = 0; attemptsSpent < 50; attemptsSpent++)
            {
                await _pollableStore.RecordDispatchAttempt(stored["id-1"], DateTime.UtcNow.AddMinutes(-5));
            }

            _reliabilityOptions.OutboxMaxDispatchAttempts.Should().BeNull();
            var polled = await _sut.GetUnprocessedMessagesFromOutbox();

            polled.Select(m => m.MessageId).Should().Equal("id-1");
        }

        [Fact]
        public async Task MustSpendNoBatchSlotOnAMessageThatHasSpentTheAttemptCeiling()
        {
            // INVARIANT: the ceiling clause, like the due clause, is applied BEFORE the batch cap, so abandoned
            // messages do not shrink the batch the poll returns.
            var stored = await SeedOutboxAsync(("id-1", 0), ("id-2", 1), ("id-3", 2));
            _reliabilityOptions.OutboxMaxDispatchAttempts = 1;
            await _pollableStore.RecordDispatchAttempt(stored["id-1"], DateTime.UtcNow.AddMinutes(-5));
            _reliabilityOptions.OutboxPollBatchSize = 2;

            var polled = await _sut.GetUnprocessedMessagesFromOutbox();

            polled.Select(m => m.MessageId).Should().Equal("id-2", "id-3");
        }

        [Fact]
        public async Task MustCountOneMoreDispatchAttemptWhenRecordingAnAttempt()
        {
            // INVARIANT: a staged message has spent no attempts, and recording one spends exactly one.
            var stored = await SeedOutboxAsync(("id-1", 0));
            stored["id-1"].DispatchAttempts.Should().Be(0);

            await _pollableStore.RecordDispatchAttempt(stored["id-1"], DateTime.UtcNow.AddMinutes(-5));

            stored["id-1"].DispatchAttempts.Should().Be(1);
        }

        [Fact]
        public async Task MustAccumulateDispatchAttemptsAcrossRecordedAttempts()
        {
            // INVARIANT: the count RISES by one per recorded attempt rather than being set to one, which is what
            // lets ReliabilityOptions.CalculateDispatchBackoff lengthen the wait and what the ceiling counts.
            var stored = await SeedOutboxAsync(("id-1", 0));

            await _pollableStore.RecordDispatchAttempt(stored["id-1"], DateTime.UtcNow.AddMinutes(-5));
            await _pollableStore.RecordDispatchAttempt(stored["id-1"], DateTime.UtcNow.AddMinutes(-5));
            await _pollableStore.RecordDispatchAttempt(stored["id-1"], DateTime.UtcNow.AddMinutes(-5));

            stored["id-1"].DispatchAttempts.Should().Be(3);
        }

        [Fact]
        public async Task MustHoldTheRecordedAttemptStateOnTheStoredRow()
        {
            // INVARIANT: the attempt state written here survives into the NEXT poll, because this store's rows
            // live for the process and a poll hands back the stored instances themselves. A store that recorded
            // the attempt onto a copy would keep re-attempting on every poll, exactly as the no-op default does.
            var nextAttemptAtUtc = DateTime.UtcNow.AddMinutes(-5);
            var stored = await SeedOutboxAsync(("id-1", 0));

            await _pollableStore.RecordDispatchAttempt(stored["id-1"], nextAttemptAtUtc);

            var polled = (await _sut.GetUnprocessedMessagesFromOutbox()).Single();
            polled.DispatchAttempts.Should().Be(1);
            polled.NextAttemptAtUtc.Should().Be(nextAttemptAtUtc);
        }

        [Fact]
        public async Task MustLeaveTheBatchQueryUngatedByDuenessAndAttempts()
        {
            // INVARIANT: GetUnprocessedBatch is a lookup by batch id, NOT an Outbox Poll Batch: neither the due
            // clause nor the ceiling applies to it. Its caller runs it once per unit of work with no re-poll loop
            // behind it, so withholding a row there drops it for good rather than deferring it.
            var transactionId = Guid.NewGuid();
            var batchContext = new TransactionContext();
            batchContext.Container.Include<IPersistanceTransaction>(StubTransaction(transactionId));
            await _sut.SendToOutbox(CreateOutbound("id-1"), batchContext);
            _reliabilityOptions.OutboxMaxDispatchAttempts = 1;
            var staged = (await _sut.GetUnprocessedBatch(transactionId)).Single();

            await _pollableStore.RecordDispatchAttempt(staged, DateTime.UtcNow.AddMinutes(5));

            var batch = await _sut.GetUnprocessedBatch(transactionId);

            batch.Should().ContainSingle().Which.MessageId.Should().Be("id-1");
        }

        // Sends each message then overwrites its SentToOutboxAtUtc, because SendToOutbox stamps DateTime.UtcNow
        // and rapid sequential sends can tie. An explicit minute per message makes the expected poll order exact.
        // Returns the STORED rows, which are the very instances the store hands a poll, so a test can set the
        // attempt state of a named message without going through a poll it may be about to be excluded from.
        private async Task<IDictionary<string, OutboxMessage>> SeedOutboxAsync(params (string MessageId, int SentAtMinute)[] seeds)
        {
            foreach (var seed in seeds)
            {
                await _sut.SendToOutbox(CreateOutbound(seed.MessageId), new TransactionContext());
            }

            var stored = (await _sut.GetUnprocessedMessagesFromOutbox()).ToDictionary(m => m.MessageId);
            foreach (var seed in seeds)
            {
                stored[seed.MessageId].SentToOutboxAtUtc = new DateTime(2026, 6, 7, 0, seed.SentAtMinute, 0, DateTimeKind.Utc);
            }

            return stored;
        }

        private static IPersistanceTransaction StubTransaction(Guid transactionId)
        {
            var transaction = new Mock<IPersistanceTransaction>();
            transaction.SetupGet(t => t.TransactionId).Returns(transactionId);
            return transaction.Object;
        }
    }
}
