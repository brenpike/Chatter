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

            // Moq PROXIES a default interface implementation rather than inheriting it, and a LOOSE mock's
            // Task<bool> answers FALSE - so without this the drain claim is DENIED and every assertion that reads a
            // drain which actually ran fails for a reason that has nothing to do with what it pins. Removing this
            // grant reddens SIXTEEN facts in this fixture (measured). The granted claim is the uncontended baseline
            // the rest of the fixture is written against; the facts that deny it do so deliberately, one at a time.
            GrantTheDrainClaim();

            _sut = new OutboxProcessor(_infrastructureProvider.Object, _logger.Object, _bodyConverterFactory.Object, _outbox.Object);
        }

        // The MessageContext column persists an IDictionary<string, object> as a JSON object string.
        // Process swallows a failed read into _logger.LogError, so dispatch would silently never fire. These
        // positive-dispatch assertions are what make such a break visible — a "does not throw" assertion alone
        // would still pass.
        private static string NewtonsoftSerializedContext()
            => $"{{\"{MessageContext.ContentType}\":\"{ContentType}\",\"{MessageContext.InfrastructureType}\":\"{Infra}\"}}";

        private static OutboxMessage CreateOutboxMessage()
            => new OutboxMessage
            {
                Id = 1,
                MessageId = "message-id",
                Destination = "destination",
                // Left empty so Process falls through to messageContext[ContentType].
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

        // EXIT 2 - DISPATCH SUCCEEDED AND THE CLAIM COMMIT THREW, AND THE RE-CLAIM FAILED TOO. A published message
        // whose row cannot be claimed at all must cost an attempt: the message IS on the broker, so a row that came
        // back due immediately would be published a second time on the very next poll.
        [Fact]
        public async Task MustRecordADispatchAttemptWhenTheReClaimAlsoFails()
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

        private int _claimAttempts;

        /// <summary>
        /// Fails the FIRST claim the way a claim commit fails, then lets the next one through and stamps the row the
        /// way a real Pollable Outbox Store stamps it - so the re-claim is read off the row a later poll would read.
        /// </summary>
        private void FailTheFirstClaimThenStampTheRow()
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.UpdateProcessedDate(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
                      .Returns<OutboxMessage, CancellationToken>(StampTheRowUnlessItIsTheFirstClaim);

        private Task StampTheRowUnlessItIsTheFirstClaim(OutboxMessage row, CancellationToken cancellationToken)
        {
            if (++_claimAttempts == 1)
            {
                throw new InvalidOperationException("the claim commit failed deliberately");
            }

            row.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        // EXIT 2 - DISPATCH SUCCEEDED AND THE CLAIM COMMIT THREW, AND THE RE-CLAIM WORKED. The row is claimed by a
        // second claim the drain issues deliberately, not by whatever a rolled-back unit of work happened to retain.
        // The Dispatch assertion is what keeps the re-claim from being a second publish.
        [Fact]
        public async Task MustReClaimTheRowWhenTheClaimCommitFailsAfterAPublish()
        {
            var message = CreateOutboxMessage();
            FailTheFirstClaimThenStampTheRow();

            await _sut.Process(message);

            message.ProcessedFromOutboxAtUtc.Should().NotBeNull();
            _outbox.As<IPollableOutboxStore>()
                   .Verify(o => o.UpdateProcessedDate(message, It.IsAny<CancellationToken>()), Times.Exactly(2));
            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
        }

        // ...AND A ROW THE RE-CLAIM CLAIMED COSTS NOTHING. The attempt is what holds a row back from the next poll,
        // and a claimed row has nothing left to hold back; spending one here would push a due time and burn budget
        // against a configured ceiling on a drain that ended in success.
        [Fact]
        public async Task MustNotRecordADispatchAttemptWhenTheReClaimSucceeds()
        {
            var message = CreateOutboxMessage();
            RecordAttemptStateOnRecord();
            FailTheFirstClaimThenStampTheRow();

            await _sut.Process(message);

            message.DispatchAttempts.Should().Be(0);
            message.NextAttemptAtUtc.Should().BeNull();
            _outbox.As<IPollableOutboxStore>()
                   .Verify(o => o.RecordDispatchAttempt(It.IsAny<OutboxMessage>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
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
        // the drain opens exactly the ONE the dispatch ran in, and the stamp goes straight to the store outside it,
        // so no unit of work can roll the stamp back and none can flush anything the stamp did not name.
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
                   .Verify(u => u.ExecuteAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()), Times.Once);
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

        private const string DrainClaimWrite = "drain-claim";
        private const string DispatchWrite = "dispatch";
        private const string RollbackWrite = "rollback";
        private const string DispatchAttemptWrite = "dispatch-attempt";

        /// <summary>
        /// Answers the drain claim the way the shipped stores answer an uncontended one, which is the baseline every
        /// fact in this fixture that is not ABOUT the claim is written against.
        /// </summary>
        private void GrantTheDrainClaim()
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.TryClaimForDispatch(It.IsAny<OutboxMessage>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(true);

        /// <summary>
        /// Answers the drain claim the way a store answers a drain that LOST the row to another drain already
        /// publishing it.
        /// </summary>
        private void DenyTheDrainClaim()
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.TryClaimForDispatch(It.IsAny<OutboxMessage>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(false);

        // ORDERING ORACLE: the drain claim is what arbitrates which of several drains gets to publish a row, so it is
        // taken BEFORE the publish or it arbitrates nothing - a claim taken afterwards lets every drain publish and
        // only then reports which one of them was supposed to. Presence alone would not catch that, so the oracle is
        // the ORDER the two seams were reached in.
        [Fact]
        public async Task MustTakeTheDrainClaimBeforeDispatching()
        {
            var writes = new System.Collections.Generic.List<string>();
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.TryClaimForDispatch(It.IsAny<OutboxMessage>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .Callback(() => writes.Add(DrainClaimWrite))
                   .ReturnsAsync(true);
            _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null))
                       .Callback<OutboundBrokeredMessage, TransactionContext>((_, __) => writes.Add(DispatchWrite))
                       .Returns(Task.CompletedTask);

            await _sut.Process(CreateOutboxMessage());

            writes.Should().Equal(DrainClaimWrite, DispatchWrite);
        }

        // A DENIED DRAIN CLAIM PUBLISHES NOTHING. The row belongs to the drain that won the claim; publishing it here
        // too is the duplicate delivery the claim exists to prevent. The processed stamp is asserted on the row - the
        // state a later poll reads - so a drain that skipped the publish but stamped the row anyway still fails.
        [Fact]
        public async Task MustDispatchNothingWhenTheDrainClaimIsDenied()
        {
            var message = CreateOutboxMessage();
            StampProcessedDateOnMark();
            DenyTheDrainClaim();

            await _sut.Process(message);

            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Never);
            message.ProcessedFromOutboxAtUtc.Should().BeNull();
            _outbox.As<IPollableOutboxStore>()
                   .Verify(o => o.UpdateProcessedDate(message, It.IsAny<CancellationToken>()), Times.Never);
        }

        // ...AND COSTS THE ROW NOTHING. A drain that was denied never attempted the publish, so charging it an attempt
        // would push the due time of a row ANOTHER drain is publishing right now and, under a configured attempt
        // ceiling, burn a healthy row's budget on contention alone. This is why the denial RETURNS rather than
        // throwing: a throw lands in the generic catch, which spends exactly that attempt.
        [Fact]
        public async Task MustNotSpendADispatchAttemptWhenTheDrainClaimIsDenied()
        {
            var message = CreateOutboxMessage();
            RecordAttemptStateOnRecord();
            DenyTheDrainClaim();

            await _sut.Process(message);

            message.DispatchAttempts.Should().Be(0);
            message.NextAttemptAtUtc.Should().BeNull();
            _outbox.As<IPollableOutboxStore>()
                   .Verify(o => o.RecordDispatchAttempt(It.IsAny<OutboxMessage>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        /// <summary>
        /// Lets ANOTHER drain claim the row in the window between the poll that handed this message back and this
        /// drain's own claim, writing through to the very instance this drain holds - which is what the shipped
        /// in-memory store hands a poll.
        /// </summary>
        private void ClaimTheRowFromAnotherDrainAsTheUnitOfWorkOpens(OutboxMessage row, DateTime anotherDrainsClaim)
            => _outbox.As<IUnitOfWork>()
                      .Setup(u => u.ExecuteAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()))
                      .Returns<Func<CancellationToken, Task>, TransactionContext, CancellationToken>((operation, _, ct) =>
                      {
                          row.NextAttemptAtUtc = anotherDrainsClaim;
                          return operation(ct);
                      });

        // THE COMPARE-AND-SET IS MADE AGAINST WHAT THE POLL READ, not against whatever the row says by the time the
        // claim runs. A store may hand a poll THE STORED INSTANCE ITSELF, so a drain reading the observed value at
        // claim time would read the OTHER drain's claim, compare it against itself, satisfy the set and be granted
        // the row that drain is already publishing - both drains win. The oracle is the value handed to the store,
        // because a self-satisfying comparison is granted by every store and is therefore invisible in the answer.
        [Fact]
        public async Task MustClaimAgainstTheDueTimeThePollRead()
        {
            var message = CreateOutboxMessage();
            var whatThePollRead = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);
            message.NextAttemptAtUtc = whatThePollRead;
            DateTime? observed = null;
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.TryClaimForDispatch(It.IsAny<OutboxMessage>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .Callback<OutboxMessage, DateTime?, DateTime, CancellationToken>((_, observedNextAttemptAtUtc, __, ___) => observed = observedNextAttemptAtUtc)
                   .ReturnsAsync(true);
            ClaimTheRowFromAnotherDrainAsTheUnitOfWorkOpens(message, whatThePollRead.AddMinutes(5));

            await _sut.Process(message);

            observed.Should().Be(whatThePollRead);
        }

        /// <summary>
        /// Stages the drain claim on the row the way a store inside a unit of work stages it, and records the write,
        /// so the order this write and the attempt stamp land in is observable.
        /// </summary>
        private void StageTheDrainClaimOnTheRow(System.Collections.Generic.IList<string> writes)
            => _outbox.As<IPollableOutboxStore>()
                      .Setup(o => o.TryClaimForDispatch(It.IsAny<OutboxMessage>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                      .Callback<OutboxMessage, DateTime?, DateTime, CancellationToken>((row, _, claimedNextAttemptAtUtc, __) =>
                      {
                          writes.Add(DrainClaimWrite);
                          row.NextAttemptAtUtc = claimedNextAttemptAtUtc;
                      })
                      .ReturnsAsync(true);

        /// <summary>
        /// Rolls the work staged inside a failed unit of work back the way a real relational one rolls its
        /// transaction back, and records the rollback.
        /// </summary>
        private void RollBackTheDrainClaimWhenTheUnitOfWorkFails(OutboxMessage row, System.Collections.Generic.IList<string> writes)
            => _outbox.As<IUnitOfWork>()
                      .Setup(u => u.ExecuteAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()))
                      .Returns<Func<CancellationToken, Task>, TransactionContext, CancellationToken>((operation, _, ct) => RunAndRollBackTheClaim(operation, row, writes, ct));

        private static async Task RunAndRollBackTheClaim(Func<CancellationToken, Task> operation,
                                                         OutboxMessage row,
                                                         System.Collections.Generic.IList<string> writes,
                                                         CancellationToken cancellationToken)
        {
            var nextAttemptBeforeTheWork = row.NextAttemptAtUtc;

            try
            {
                await operation(cancellationToken);
            }
            catch
            {
                row.NextAttemptAtUtc = nextAttemptBeforeTheWork;
                writes.Add(RollbackWrite);
                throw;
            }
        }

        // THE TWO WRITES TO THE SAME COLUMN, IN ORDER. The drain claim and the attempt stamp both move
        // NextAttemptAtUtc, and on a failed publish they meet: the rollback discards the claim, and only then does
        // the attempt stamp land, outside the unit of work. That order is what leaves the row scheduled by the
        // FAILURE rather than by a claim the failure already voided. The oracle is the sequence of writes plus the
        // value the row is finally left at, because the claim instant and the attempt instant are both "now plus one
        // backoff" and are not told apart by reading the row alone.
        [Fact]
        public async Task MustRecordTheDispatchAttemptAfterTheRollbackDiscardsTheDrainClaim()
        {
            var message = CreateOutboxMessage();
            var writes = new System.Collections.Generic.List<string>();
            DateTime? recorded = null;
            StageTheDrainClaimOnTheRow(writes);
            _outbox.As<IPollableOutboxStore>()
                   .Setup(o => o.RecordDispatchAttempt(It.IsAny<OutboxMessage>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                   .Callback<OutboxMessage, DateTime, CancellationToken>((row, nextAttemptAtUtc, _) =>
                   {
                       writes.Add(DispatchAttemptWrite);
                       recorded = nextAttemptAtUtc;
                       row.DispatchAttempts++;
                       row.NextAttemptAtUtc = nextAttemptAtUtc;
                   })
                   .Returns(Task.CompletedTask);
            RollBackTheDrainClaimWhenTheUnitOfWorkFails(message, writes);
            FailTheDispatch();

            await _sut.Process(message);

            writes.Should().Equal(DrainClaimWrite, RollbackWrite, DispatchAttemptWrite);
            message.DispatchAttempts.Should().Be(1);
            message.NextAttemptAtUtc.Should().Be(recorded);
        }

        private const int NotAString = 42;

        /// <summary>
        /// Persists <paramref name="context"/> the way the outbox writers persist a Message Context, with the
        /// content type left off the row so the drain has to read it back out of the context.
        /// </summary>
        private static OutboxMessage CreateOutboxMessageWithContext(System.Collections.Generic.IDictionary<string, object> context)
            => new OutboxMessage
            {
                Id = 4,
                MessageId = "message-id",
                Destination = "destination",
                MessageContentType = null,
                MessageContext = System.Text.Json.JsonSerializer.Serialize(context, ChatterJson.Options),
                MessageBody = "message-body",
            };

        private void VerifyFailureLoggedAs<TException>() where TException : Exception
            => _logger.Verify(
                l => l.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<TException>(),
                    (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()),
                Times.Once);

        // A PERSISTED INFRASTRUCTURE TYPE OF THE WRONG KIND MISROUTES RATHER THAN WEDGES. The row is otherwise
        // dispatchable, so refusing it on every poll would strand it for good; the drain reads the value as absent
        // and hands the dispatch to the default Messaging Infrastructure, which is what an absent value already did.
        [Fact]
        public async Task MustDispatchViaTheDefaultInfrastructureWhenThePersistedInfrastructureTypeIsNotAString()
        {
            var message = CreateOutboxMessageWithContext(new System.Collections.Generic.Dictionary<string, object>
            {
                [MessageContext.ContentType] = ContentType,
                [MessageContext.InfrastructureType] = NotAString,
            });

            await _sut.Process(message);

            _infrastructureProvider.Verify(p => p.GetDispatcher(null), Times.Once);
            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Once);
        }

        // ...AND SAYS SO. A present-but-unreadable infrastructure type is a misroute the operator has to be able to
        // see, so it is logged naming the key rather than silently treated the way an absent one is.
        [Fact]
        public async Task MustLogThatThePersistedInfrastructureTypeWasUnreadable()
        {
            var message = CreateOutboxMessageWithContext(new System.Collections.Generic.Dictionary<string, object>
            {
                [MessageContext.ContentType] = ContentType,
                [MessageContext.InfrastructureType] = NotAString,
            });

            await _sut.Process(message);

            _logger.Verify(
                l => l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => state.ToString().Contains(MessageContext.InfrastructureType)),
                    It.IsAny<Exception>(),
                    (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()),
                Times.Once);
        }

        // A PERSISTED CONTENT TYPE OF THE WRONG KIND IS REFUSED THE WAY A MISSING ONE IS - through the classified
        // "a content type is required" refusal, not an InvalidCastException from reading it. The Dispatch assertion
        // keeps the refusal from being satisfied by a drain that published anyway.
        [Fact]
        public async Task MustRefuseAnOutboxMessageWhosePersistedContentTypeIsNotAString()
        {
            var message = CreateOutboxMessageWithContext(new System.Collections.Generic.Dictionary<string, object>
            {
                [MessageContext.ContentType] = NotAString,
                [MessageContext.InfrastructureType] = Infra,
            });

            await _sut.Process(message);

            VerifyFailureLoggedAs<ArgumentNullException>();
            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Never);
        }

        // ...AND SO IS A CONTEXT THAT CARRIES NO CONTENT TYPE AT ALL, rather than failing on a KeyNotFoundException
        // from reading a key the writer never set.
        [Fact]
        public async Task MustRefuseAnOutboxMessageWhosePersistedContentTypeKeyIsAbsent()
        {
            var message = CreateOutboxMessageWithContext(new System.Collections.Generic.Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = Infra,
            });

            await _sut.Process(message);

            VerifyFailureLoggedAs<ArgumentNullException>();
            _dispatcher.Verify(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), null), Times.Never);
        }
    }
}
