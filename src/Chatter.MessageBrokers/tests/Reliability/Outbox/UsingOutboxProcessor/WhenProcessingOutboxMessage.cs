using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Outbox.UsingOutboxProcessor
{
    public class WhenProcessingOutboxMessage : Testing.Core.Context
    {
        private const string Infra = "test-infrastructure";
        private const string ContentType = "application/json";

        private readonly Mock<IMessagingInfrastructureProvider> _infrastructureProvider = new Mock<IMessagingInfrastructureProvider>();
        private readonly Mock<IMessagingInfrastructureDispatcher> _dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
        private readonly Mock<ILogger<OutboxProcessor>> _logger = new Mock<ILogger<OutboxProcessor>>();
        private readonly Mock<IBodyConverterFactory> _bodyConverterFactory = new Mock<IBodyConverterFactory>();
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IBrokeredMessageOutbox> _outbox = new Mock<IBrokeredMessageOutbox>();
        private readonly OutboxProcessor _sut;

        public WhenProcessingOutboxMessage()
        {
            _infrastructureProvider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(_dispatcher.Object);
            _bodyConverter.SetupGet(c => c.ContentType).Returns(ContentType);
            _bodyConverter.Setup(c => c.GetBytes(It.IsAny<string>())).Returns(new byte[] { 1, 2, 3 });
            _bodyConverterFactory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(_bodyConverter.Object);

            // INVARIANT: OutboxProcessor.Process casts the outbox to IUnitOfWork and IPollableOutboxStore
            // at the consumption site; the mock must implement both via .As<T>() so the casts succeed and
            // the IUnitOfWork.ExecuteAsync callback is invoked (or dispatch never fires).
            // .As<T>() is called on both before Setup so Moq registers the multi-interface proxy once.
            _outbox.As<IPollableOutboxStore>();
            _outbox.As<IUnitOfWork>()
                   .Setup(u => u.ExecuteAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()))
                   .Returns<Func<CancellationToken, Task>, TransactionContext, CancellationToken>((operation, _, ct) => operation(ct));

            _sut = new OutboxProcessor(_infrastructureProvider.Object, _logger.Object, _bodyConverterFactory.Object, _outbox.Object);
        }

        // The MessageContext column persists a Newtonsoft-serialized IDictionary<string, object> as a
        // JSON object string. Process deserializes it via JsonConvert.DeserializeObject and then performs
        // hard (string) casts on the ContentType (:43) and InfrastructureType (:48) values. Under Newtonsoft
        // those values deserialize to System.String, so the casts succeed.
        //
        // ORACLE: under System.Text.Json, DeserializeObject<IDictionary<string, object>> yields JsonElement
        // values, and the (string) casts at :43 and :48 throw InvalidCastException. Process swallows that
        // into _logger.LogError, so dispatch would silently never fire. These positive-dispatch assertions
        // are what make that future break visible — a "does not throw" assertion alone would still pass.
        private static string NewtonsoftSerializedContext()
            => $"{{\"{MessageContext.ContentType}\":\"{ContentType}\",\"{MessageContext.InfrastructureType}\":\"{Infra}\"}}";

        private static OutboxMessage CreateOutboxMessage()
            => new OutboxMessage
            {
                Id = 1,
                MessageId = "message-id",
                Destination = "destination",
                // Left empty so Process falls through to the (string) cast on messageContext[ContentType] at :43.
                MessageContentType = null,
                MessageContext = NewtonsoftSerializedContext(),
                MessageBody = "message-body",
            };

        [Fact]
        public async Task MustResolveDispatcherUsingInfrastructureTypeFromDeserializedContext()
        {
            await _sut.Process(CreateOutboxMessage());

            _infrastructureProvider.Verify(p => p.GetDispatcher(Infra), Times.Once);
        }

        [Fact]
        public async Task MustResolveBodyConverterUsingContentTypeFromDeserializedContext()
        {
            await _sut.Process(CreateOutboxMessage());

            _bodyConverterFactory.Verify(f => f.CreateBodyConverter(ContentType), Times.Once);
        }

        [Fact]
        public async Task MustDispatchOutboundMessageToInfrastructure()
        {
            await _sut.Process(CreateOutboxMessage());

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
        }

        [Fact]
        public async Task MustMarkOutboxMessageProcessed()
        {
            var message = CreateOutboxMessage();

            await _sut.Process(message);

            _outbox.As<IPollableOutboxStore>().Verify(o => o.UpdateProcessedDate(message, It.IsAny<CancellationToken>()), Times.Once);
        }

        /// <summary>
        /// Stamps <see cref="OutboxMessage.ProcessedFromOutboxAtUtc"/> the way a real Pollable Outbox Store stamps
        /// it, so an assertion on the row reads the state a later poll would read rather than a mock's unset default.
        /// </summary>
        private void StampProcessedDateOnMark()
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.UpdateProcessedDate(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
                      .Callback<OutboxMessage, CancellationToken>((m, _) => m.ProcessedFromOutboxAtUtc = DateTime.UtcNow)
                      .Returns(Task.CompletedTask);

        // ORDERING ORACLE: the drain must publish BEFORE it records the row processed. Marking first records a
        // message that never reached the broker as delivered, so the failed row is never polled again and is lost
        // permanently. The oracle is the SINK, never a copy of the production condition: the store's own
        // UpdateProcessedDate is what stamps the processed date, so the assertion reads the row state the next poll
        // would read. The dispatch verification keeps it non-vacuous - a drain that stopped publishing at all would
        // otherwise satisfy the unprocessed assertions.
        [Fact]
        public async Task MustLeaveOutboxMessageUnprocessedWhenDispatchFails()
        {
            var message = CreateOutboxMessage();
            StampProcessedDateOnMark();
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null))
                       .ThrowsAsync(new InvalidOperationException("the broker publish failed deliberately"));

            await _sut.Process(message);

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
            message.ProcessedFromOutboxAtUtc.Should().BeNull();
            _outbox.As<IPollableOutboxStore>().Verify(o => o.UpdateProcessedDate(message, It.IsAny<CancellationToken>()), Times.Never);
        }

        // ERROR-POSTURE LOCK: a failed publish stays logged-and-swallowed. Process is driven by the outbox poll and
        // by OutboxProcessingBehavior, so rethrowing would push a broker outage into the CQRS pipeline. This test is
        // green both before and after the ordering fix by design - it exists so the fix cannot quietly change the
        // posture along with the order.
        [Fact]
        public async Task MustNotThrowWhenDispatchFails()
        {
            StampProcessedDateOnMark();
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null))
                       .ThrowsAsync(new InvalidOperationException("the broker publish failed deliberately"));

            Func<Task> process = () => _sut.Process(CreateOutboxMessage());

            await process.Should().NotThrowAsync();
        }

        // REGRESSION ORACLE: production writers (InMemory/EF SendToOutbox) serialize the entire
        // IDictionary<string, object> MessageContext, which legitimately holds non-string values —
        // an integer ReceiveAttempts (SSB receive/deadletter), a TimeSpan TimeToLive, and a DateTime
        // ScheduledEnqueueTimeUtc (Azure). Deserializing that row back to Dictionary<string, string>
        // threw JsonException on the numeric value; Process swallows it into LogError, silently
        // stranding a valid row (never dispatched, never marked processed). This context is built the
        // same way the writers build it — JsonSerializer.Serialize over an IDictionary<string, object>
        // with ChatterJson.Options — so it pins the real wire format, and the positive-dispatch +
        // processed assertions make any future re-break of the materialization visible.
        private static OutboxMessage CreateOutboxMessageWithNonStringContextValues()
        {
            var context = new System.Collections.Generic.Dictionary<string, object>
            {
                [MessageContext.ContentType] = ContentType,
                [MessageContext.InfrastructureType] = Infra,
                [MessageContext.ReceiveAttempts] = 3,
                [MessageContext.TimeToLive] = TimeSpan.FromMinutes(5),
                ["ScheduledEnqueueTimeUtc"] = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc),
            };

            return new OutboxMessage
            {
                Id = 2,
                MessageId = "message-id",
                Destination = "destination",
                MessageContentType = null,
                MessageContext = System.Text.Json.JsonSerializer.Serialize(context, ChatterJson.Options),
                MessageBody = "message-body",
            };
        }

        [Fact]
        public async Task MustDispatchOutboxMessageWhenContextContainsNonStringValues()
        {
            await _sut.Process(CreateOutboxMessageWithNonStringContextValues());

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
        }

        [Fact]
        public async Task MustMarkProcessedWhenContextContainsNonStringValues()
        {
            var message = CreateOutboxMessageWithNonStringContextValues();

            await _sut.Process(message);

            _outbox.As<IPollableOutboxStore>().Verify(o => o.UpdateProcessedDate(message, It.IsAny<CancellationToken>()), Times.Once);
        }

        // STRUCTURED-VALUE REPLAY FIDELITY (the "all areas" mandate for the outbox seam): a MessageContext
        // value that is itself a STRUCTURED object survives the persist -> replay round-trip materialized
        // to a navigable Dictionary<string, object> with CLR-typed leaves (NOT a raw JsonElement). The
        // OutboxProcessor builds the replayed OutboundBrokeredMessage from
        // MessageContext.MaterializePersistedContext(message.MessageContext), whose values are driven by
        // the global MaterializingObjectConverter on ChatterJson.Options. Capturing the dispatched message
        // exposes the materialized context so a regression that re-surfaced a JsonElement here fails.
        [Fact]
        public async Task MustReplayStructuredContextValueMaterializedOnDispatch()
        {
            var context = new System.Collections.Generic.Dictionary<string, object>
            {
                [MessageContext.ContentType] = ContentType,
                [MessageContext.InfrastructureType] = Infra,
                ["structured"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["id"] = 1,
                    ["name"] = "abc",
                },
            };

            var message = new OutboxMessage
            {
                Id = 3,
                MessageId = "message-id",
                Destination = "destination",
                MessageContentType = null,
                MessageContext = System.Text.Json.JsonSerializer.Serialize(context, ChatterJson.Options),
                MessageBody = "message-body",
            };

            OutboundBrokeredMessage dispatched = null;
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null))
                       .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => dispatched = m)
                       .Returns(Task.CompletedTask);

            await _sut.Process(message);

            dispatched.Should().NotBeNull();
            dispatched.MessageContext.Should().ContainKey("structured");

            var structured = dispatched.MessageContext["structured"]
                .Should().BeAssignableTo<System.Collections.Generic.IDictionary<string, object>>().Subject;
            structured["id"].Should().BeOfType<long>().And.Be(1L);
            structured["name"].Should().BeOfType<string>().And.Be("abc");
        }

        /// <summary>
        /// Records the attempt state on the row the way <c>InMemoryBrokeredMessageOutbox.RecordDispatchAttempt</c>
        /// records it - through to the stored row - so an assertion on the row reads the state a later poll would
        /// read rather than a mock's unset default.
        /// </summary>
        private void RecordAttemptStateOnRecord()
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.RecordDispatchAttempt(It.IsAny<OutboxMessage>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                      .Callback<OutboxMessage, DateTime, CancellationToken>((m, nextAttemptAtUtc, _) =>
                      {
                          m.DispatchAttempts++;
                          m.NextAttemptAtUtc = nextAttemptAtUtc;
                      })
                      .Returns(Task.CompletedTask);

        private void FailTheDispatch()
            => _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null))
                          .ThrowsAsync(new InvalidOperationException("the broker publish failed deliberately"));

        // EXIT 1 - DISPATCH THREW. A row whose publish failed must cost an attempt and be scheduled forward, or the
        // due gate the poll now applies never holds anything back and a permanently-failing row keeps its place at
        // the head of every batch.
        [Fact]
        public async Task MustRecordADispatchAttemptWhenDispatchFails()
        {
            var message = CreateOutboxMessage();
            FailTheDispatch();

            await _sut.Process(message);

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
            _outbox.As<IPollableOutboxStore>()
                   .Verify(o => o.RecordDispatchAttempt(message, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        // EXIT 2 - DISPATCH SUCCEEDED AND THE CLAIM COMMIT THREW. This exit is what makes the due gate subsume a
        // duplicate dispatch: the message IS on the broker, so a row that came back due immediately would be
        // published a second time on the very next poll. It costs an attempt exactly like a failed publish.
        [Fact]
        public async Task MustRecordADispatchAttemptWhenMarkingProcessedFails()
        {
            var message = CreateOutboxMessage();
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.UpdateProcessedDate(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("the claim commit failed deliberately"));

            await _sut.Process(message);

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
            _outbox.As<IPollableOutboxStore>()
                   .Verify(o => o.RecordDispatchAttempt(message, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        // SCHEDULE - THE FIRST FAILURE. The instant handed to the store is one backoff ahead of now, taken from
        // ReliabilityOptions rather than invented here: a drain built without options must still back off by the
        // shipped base of 5 seconds. The Kind assertion pins that a UTC instant is what reaches a UTC column.
        [Fact]
        public async Task MustScheduleTheNextAttemptOneBackoffAhead()
        {
            var message = CreateOutboxMessage();
            DateTime? scheduled = null;
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.RecordDispatchAttempt(It.IsAny<OutboxMessage>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .Callback<OutboxMessage, DateTime, CancellationToken>((_, nextAttemptAtUtc, __) => scheduled = nextAttemptAtUtc)
                   .Returns(Task.CompletedTask);
            FailTheDispatch();

            await _sut.Process(message);

            scheduled.Should().NotBeNull();
            scheduled.Value.Kind.Should().Be(DateTimeKind.Utc);
            scheduled.Value.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(5), TimeSpan.FromSeconds(2));
        }

        // SCHEDULE - THE WAIT GROWS WITH THE ROW'S OWN ATTEMPT COUNT. The count handed to the backoff is the count
        // THIS failure leaves the row at, so the waits run 5s, 10s, 20s rather than repeating the base. A row that
        // has already failed twice is scheduled 5 * 2^2 = 20 seconds out; passing the row's pre-increment count
        // instead would schedule it 10 seconds out.
        [Fact]
        public async Task MustGrowTheScheduledWaitWithTheAttemptsTheRowAlreadyCarries()
        {
            var message = CreateOutboxMessage();
            message.DispatchAttempts = 2;
            DateTime? scheduled = null;
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.RecordDispatchAttempt(It.IsAny<OutboxMessage>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .Callback<OutboxMessage, DateTime, CancellationToken>((_, nextAttemptAtUtc, __) => scheduled = nextAttemptAtUtc)
                   .Returns(Task.CompletedTask);
            FailTheDispatch();

            await _sut.Process(message);

            scheduled.Should().NotBeNull();
            scheduled.Value.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(20), TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Gives the mocked Unit of Work the one behaviour that matters to the attempt stamp: work staged inside an
        /// operation that throws is ROLLED BACK, the way a real relational unit of work rolls its transaction back.
        /// </summary>
        private void RollBackAttemptStateStagedInsideAFailedUnitOfWork(OutboxMessage row)
            => _outbox.As<IUnitOfWork>()
                      .Setup(u => u.ExecuteAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()))
                      .Returns<Func<CancellationToken, Task>, TransactionContext, CancellationToken>((operation, _, ct) => RunAndRollBackOnFailure(operation, row, ct));

        private static async Task RunAndRollBackOnFailure(Func<CancellationToken, Task> operation, OutboxMessage row, CancellationToken cancellationToken)
        {
            var attemptsBeforeTheWork = row.DispatchAttempts;
            var nextAttemptBeforeTheWork = row.NextAttemptAtUtc;

            try
            {
                await operation(cancellationToken);
            }
            catch
            {
                row.DispatchAttempts = attemptsBeforeTheWork;
                row.NextAttemptAtUtc = nextAttemptBeforeTheWork;
                throw;
            }
        }

        // THE STAMP MUST SURVIVE THE ROLLBACK. Dispatch runs inside a unit of work that rolls back when it throws,
        // so an attempt staged in THAT unit of work is discarded with it and the whole mechanism silently does
        // nothing. The oracle is the row - the state a later poll reads - plus the count of units of work opened:
        // the stamp travels through a SECOND one of its own.
        [Fact]
        public async Task MustRecordTheDispatchAttemptOutsideTheRolledBackUnitOfWork()
        {
            var message = CreateOutboxMessage();
            RecordAttemptStateOnRecord();
            RollBackAttemptStateStagedInsideAFailedUnitOfWork(message);
            FailTheDispatch();

            await _sut.Process(message);

            message.DispatchAttempts.Should().Be(1);
            message.NextAttemptAtUtc.Should().NotBeNull();
            _outbox.As<IUnitOfWork>()
                   .Verify(u => u.ExecuteAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        // CANCELLATION IS EXEMPT. A drain stopped by shutdown never attempted anything the broker refused, so
        // spending an attempt on it would push a perfectly good row's due time out - and, with a ceiling
        // configured, would burn the row's budget on restarts alone.
        [Fact]
        public async Task MustNotRecordADispatchAttemptWhenProcessingIsCancelled()
        {
            var message = CreateOutboxMessage();
            using var shutdown = new CancellationTokenSource();
            shutdown.Cancel();
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null))
                       .ThrowsAsync(new OperationCanceledException(shutdown.Token));

            await _sut.Process(message, shutdown.Token);

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
            _outbox.As<IPollableOutboxStore>()
                   .Verify(o => o.RecordDispatchAttempt(It.IsAny<OutboxMessage>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        // ...AND ONLY SHUTDOWN IS EXEMPT. A cancellation raised by some OTHER token - an infrastructure client's own
        // send timeout - is a dispatch that failed, and is recorded as one. This is why the exemption is filtered on
        // the drain's own token rather than written as a bare catch of OperationCanceledException.
        [Fact]
        public async Task MustRecordADispatchAttemptWhenDispatchIsCancelledByAnotherToken()
        {
            var message = CreateOutboxMessage();
            using var brokerClientTimeout = new CancellationTokenSource();
            brokerClientTimeout.Cancel();
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null))
                       .ThrowsAsync(new OperationCanceledException(brokerClientTimeout.Token));

            await _sut.Process(message);

            _outbox.As<IPollableOutboxStore>()
                   .Verify(o => o.RecordDispatchAttempt(message, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        // ERROR-POSTURE LOCK FOR THE STAMP ITSELF. A store that cannot record the attempt leaves the row exactly as
        // it is today - due now, zero attempts, retried next drain - so this change can never leave the drain worse
        // than the behaviour it replaced. Rethrowing here would push a store outage into the CQRS pipeline through
        // OutboxProcessingBehavior, which the swallowed publish failure deliberately does not do.
        [Fact]
        public async Task MustNotThrowWhenRecordingTheDispatchAttemptFails()
        {
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.RecordDispatchAttempt(It.IsAny<OutboxMessage>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("the attempt write failed deliberately"));
            FailTheDispatch();

            Func<Task> process = () => _sut.Process(CreateOutboxMessage());

            await process.Should().NotThrowAsync();
            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
        }
    }
}
