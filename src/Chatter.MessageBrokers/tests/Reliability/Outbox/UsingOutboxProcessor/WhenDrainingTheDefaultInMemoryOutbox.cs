using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Outbox.UsingOutboxProcessor
{
    /// <summary>
    /// Drains the REAL default in-memory Pollable Outbox Store through the REAL <see cref="OutboxProcessor"/>.
    /// </summary>
    /// <remarks>
    /// INVARIANT: no mock outbox. The sibling drain tests build their store as a mock and grant it the Unit of Work
    /// facet with <c>As&lt;IUnitOfWork&gt;()</c>, which manufactures the very facet whose absence broke the DI-default
    /// store: the cast in <see cref="OutboxProcessor.Process"/> threw into the swallowing catch, nothing was ever
    /// dispatched, and the suite stayed green over a dead default outbox. This class wires the concrete store DI
    /// registers so no facet can be faked, and the only test double is the messaging infrastructure the drain
    /// publishes to.
    /// ORACLE: every assertion reads the SINK - the messages the infrastructure dispatcher actually received, the
    /// rows the store itself still returns from GetUnprocessedMessagesFromOutbox, and the attempt state carried by
    /// a polled row, which is the stored instance itself because the store records attempts write-through - never a
    /// copy of the drain's own condition and never the store's private state.
    /// </remarks>
    public class WhenDrainingTheDefaultInMemoryOutbox : Testing.Core.Context
    {
        private const string Infra = "test-infrastructure";
        private const string Destination = "destination";
        private const string MessageId = "message-id";
        private const string MessageBody = "payload";

        private readonly RecordingInfrastructureDispatcher _dispatcher = new RecordingInfrastructureDispatcher();
        private readonly InMemoryBrokeredMessageOutbox _outbox;
        private readonly IPollableOutboxStore _pollableStore;
        private readonly OutboxProcessor _sut;

        public WhenDrainingTheDefaultInMemoryOutbox()
        {
            _outbox = new InMemoryBrokeredMessageOutbox(New.Common().RecordingLogger<InMemoryBrokeredMessageOutbox>().Creation,
                                                        new ReliabilityOptions());
            _pollableStore = _outbox;
            _sut = CreateDrainOver(_outbox);
        }

        /// <summary>
        /// Wires a drain over the store handed in, publishing to the one recording dispatcher every fact reads as
        /// its delivery sink. Two drains built over the same store therefore compete for its rows and report to the
        /// same sink, which is what lets a fact count the publishes a raced row produced.
        /// </summary>
        private OutboxProcessor CreateDrainOver(IBrokeredMessageOutbox outbox, ReliabilityOptions reliabilityOptions = null)
        {
            var infrastructureProvider = new MessagingInfrastructureProvider(new IMessagingInfrastructure[] { new DispatchOnlyInfrastructure(Infra, _dispatcher) },
                                                                            New.Common().RecordingLogger<MessagingInfrastructureProvider>().Creation);
            var bodyConverterFactory = new BodyConverterFactory(new IBrokeredMessageBodyConverter[] { new JsonBodyConverter() });

            return new OutboxProcessor(infrastructureProvider,
                                       New.Common().RecordingLogger<OutboxProcessor>().Creation,
                                       bodyConverterFactory,
                                       outbox,
                                       reliabilityOptions);
        }

        /// <summary>
        /// Writes one row the way a sender writes it - through the store's own SendToOutbox - and polls it back the
        /// way the drain polls it, so the row under test is one the production write path produced.
        /// </summary>
        private Task<OutboxMessage> EnqueueAndPollOneRow()
            => EnqueueAndPollOneRow(_outbox);

        private async Task<OutboxMessage> EnqueueAndPollOneRow(InMemoryBrokeredMessageOutbox outbox)
        {
            var messageContext = new Dictionary<string, object> { [MessageContext.InfrastructureType] = Infra };
            var outbound = new OutboundBrokeredMessage(MessageId, MessageBody, messageContext, Destination, new JsonBodyConverter());

            await outbox.SendToOutbox(outbound, null);

            return (await ((IPollableOutboxStore)outbox).GetUnprocessedMessagesFromOutbox()).Single();
        }

        private async Task<IEnumerable<OutboxMessage>> PollUnprocessedRows()
            => await _pollableStore.GetUnprocessedMessagesFromOutbox();

        // FACT 1 - HAPPY PATH (delivery). The sink is the infrastructure dispatcher: it holds the message ids it was
        // handed, so a drain that resolved no dispatcher, threw on a facet cast, or swallowed anything on the way
        // delivers nothing and this fails.
        [Fact]
        public async Task MustDispatchTheDrainedRowExactlyOnce()
        {
            var row = await EnqueueAndPollOneRow();

            await _sut.Process(row);

            _dispatcher.DeliveredMessageIds.Should().ContainSingle().Which.Should().Be(MessageId);
        }

        // FACT 1 - HAPPY PATH (the row leaves the poll). The sink is the store's own query: a dispatched row must no
        // longer be returned to a later poll, or the drain republishes it forever.
        [Fact]
        public async Task MustStopReturningTheDrainedRowAsUnprocessed()
        {
            var row = await EnqueueAndPollOneRow();

            await _sut.Process(row);

            (await PollUnprocessedRows()).Should().BeEmpty();
        }

        // FACT 2 - FAILURE IS RECORDED AND STILL RETRYABLE. A publish that never reached the broker must leave the
        // row exactly as a later poll needs to find it: unclaimed, one attempt poorer, and scheduled a backoff
        // ahead so the poll's due gate holds it back rather than handing it to every batch. The row read here IS
        // the row the store keeps - RecordDispatchAttempt writes through to the stored instance - so these read the
        // state the next poll reads. The dispatch-attempt assertion keeps this non-vacuous: a drain that stopped
        // publishing at all would otherwise satisfy the unprocessed assertion. The schedule is asserted as a bound
        // rather than as merely present, so a row parked at an instant no poll ever reaches fails here too.
        [Fact]
        public async Task MustRecordTheFailedDispatchAndLeaveTheRowUnprocessed()
        {
            var row = await EnqueueAndPollOneRow();
            _dispatcher.FailureToThrow = new InvalidOperationException("the broker publish failed deliberately");

            await _sut.Process(row);

            _dispatcher.DispatchAttempts.Should().Be(1);
            _dispatcher.DeliveredMessageIds.Should().BeEmpty();
            row.ProcessedFromOutboxAtUtc.Should().BeNull();
            row.DispatchAttempts.Should().Be(1);
            row.NextAttemptAtUtc.Should().NotBeNull();
            row.NextAttemptAtUtc.Value.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(5), TimeSpan.FromSeconds(2));
        }

        // FACT 2 - ERROR POSTURE. Process is driven by the outbox poll and by OutboxProcessingBehavior, so a broker
        // outage stays logged-and-swallowed rather than surfacing in the CQRS pipeline.
        [Fact]
        public async Task MustNotThrowWhenDispatchFails()
        {
            var row = await EnqueueAndPollOneRow();
            _dispatcher.FailureToThrow = new InvalidOperationException("the broker publish failed deliberately");

            Func<Task> process = () => _sut.Process(row);

            await process.Should().NotThrowAsync();
        }

        // FACT 3 - RECOVERY ONCE DUE. The row a failed drain left behind is the row a LATER poll hands back, and a
        // healthy dispatcher then delivers THAT row and the store stops returning it. This is the whole point of
        // leaving the row unprocessed, so it is asserted end to end rather than inferred from fact 2. The failed
        // attempt schedules the row a backoff ahead, so the fact first pins that the row is genuinely held back
        // while that wait stands and then elapses the wait - standing in for wall-clock time, and touching nothing
        // about the row but the instant - so what it pins is re-dispatch once due rather than immediate re-dispatch.
        [Fact]
        public async Task MustDispatchTheSameRowOnTheNextDrainOnceItsBackoffHasElapsed()
        {
            var row = await EnqueueAndPollOneRow();
            _dispatcher.FailureToThrow = new InvalidOperationException("the broker publish failed deliberately");
            await _sut.Process(row);
            _dispatcher.FailureToThrow = null;

            (await PollUnprocessedRows()).Should().BeEmpty();
            row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);

            var retriedRow = (await PollUnprocessedRows()).Single();
            await _sut.Process(retriedRow);

            retriedRow.MessageId.Should().Be(MessageId);
            _dispatcher.DispatchAttempts.Should().Be(2);
            _dispatcher.DeliveredMessageIds.Should().ContainSingle().Which.Should().Be(MessageId);
            (await PollUnprocessedRows()).Should().BeEmpty();
            retriedRow.ProcessedFromOutboxAtUtc.Should().NotBeNull();
        }

        // FACT 4 - TWO DRAINS RACE ONE ROW. Two drains that polled the same row - and it is literally one row, since
        // this store hands a poll the stored instance itself - both reach the broker unless the DRAIN CLAIM
        // arbitrates between them. This is the whole mechanism end to end: the real store, the real drain, and
        // nothing stubbed between them but the broker. The interleaving is FORCED rather than raced - the store's
        // claim seam runs the competing drain to completion at exactly the point between this drain's observation
        // of the row's due instant and its compare-and-set, the only window in which both could be granted - so the
        // fact is single-threaded and deterministic and asserts nothing about timing.
        // ORACLE: the count at the DELIVERY SINK - how many times the message reached the messaging infrastructure.
        // A row count is no oracle here: the store keys rows by message id, so one row is guaranteed by the
        // dictionary and could never fail.
        // The loser here is refused by the PROCESSED conjunct of the claim, because the competing drain ran all the
        // way through UpdateProcessedDate before this drain resumed. MustCostTheRowNothingWhenTheDrainLosesTheRace
        // is the fact whose loser is refused by the comparison against the instant its poll reported.
        // MEASURED, by running this project's facts against each mutation in turn:
        // - Granting the in-memory claim unconditionally reddens 7 facts, this one among them and ON ITS SINK
        //   (two publishes where one was expected); the other six are the store's own claim facts.
        // - Hoisting the claim's refusal decision ABOVE the seam - a read, then the window, then the write, which
        //   is the defect's own shape in a new place - reddens 3: this fact and the next, both ON THEIR SINK, and
        //   MustGrantExactlyOneOfTwoCompetingDrainClaims.
        // - Deleting the claim call from OutboxProcessor.Process reddens 6: this fact and the next, plus four
        //   WhenProcessingOutboxMessage claim facts. This one reddens on the forcing-function guard rather than on
        //   the sink, because the seam lives inside the claim and deleting the claim deletes the window itself -
        //   which is exactly what that guard is for: without it the fact would pass by never having raced.
        // - Dropping the monitor while leaving the read and the write in order reddens NOTHING in this project.
        //   No deterministic single-threaded fact can see a missing lock, and this one does not pretend to.
        [Fact]
        public async Task MustDispatchTheRacedRowExactlyOnce()
        {
            var competingDrainHasRun = false;
            InMemoryBrokeredMessageOutbox racedOutbox = null;
            OutboxProcessor competingDrain = null;
            racedOutbox = CreateOutboxRacedBeforeEachClaim(_ =>
            {
                if (competingDrainHasRun)
                {
                    return;
                }
                competingDrainHasRun = true;
                DrainOneRowToCompletion(competingDrain, racedOutbox);
            });
            competingDrain = CreateDrainOver(racedOutbox);
            var racingDrain = CreateDrainOver(racedOutbox);
            var racedRow = await EnqueueAndPollOneRow(racedOutbox);

            await racingDrain.Process(racedRow);

            competingDrainHasRun.Should().BeTrue();
            _dispatcher.DispatchAttempts.Should().Be(1);
            _dispatcher.DeliveredMessageIds.Should().ContainSingle().Which.Should().Be(MessageId);
        }

        // FACT 4 - THE LOSING DRAIN COSTS THE ROW NOTHING. A denied drain publishes nothing, stamps nothing and
        // spends no dispatch attempt: an arbitration that charged the loser an attempt would push the row's next
        // attempt out on the strength of a race alone and, under a configured attempt ceiling, retire it. The row
        // it leaves behind is still polled and still dispatchable, which is the failure strictly worse than the
        // duplicate this arbitration exists to prevent - FACT 3 above pins the dispatch of such a row end to end,
        // so this fact stops at pinning that the denial left one for it to take.
        // The competing drain's publish FAILS here, so it leaves the row unprocessed rather than stamped, and its
        // wait is then elapsed - standing in for wall-clock time the way FACT 3 does - so the row is due again by
        // the time this drain's claim resumes. The denial is therefore made by the comparison against the instant
        // THIS drain's poll reported, and neither by the processed stamp nor by the claim's due gate, both of which
        // would otherwise refuse the row first and mask that comparison.
        // ORACLE: the attempt counts on both sides of the publish - the dispatcher's, which counts every publish a
        // drain began, and the row's, which counts every attempt the store recorded. Both are one, because only the
        // competing drain ever attempted anything.
        // MEASURED the same way: under each of the three mutations that redden MustDispatchTheRacedRowExactlyOnce
        // above, this fact reddens too and on whichever of the two oracles that one reddens on - and like it, a
        // dropped monitor leaves it green. It additionally reddens where that one does not: removing the claim's comparison against the reported instant reddens 3 facts - this
        // one and the store's MustGrantExactlyOneOfTwoCompetingDrainClaims and
        // MustRefuseADrainClaimAgainstAStaleObservedDueInstant - which is what establishes that the comparison, and
        // not the processed stamp or the due gate, is what refuses the loser here.
        [Fact]
        public async Task MustCostTheRowNothingWhenTheDrainLosesTheRace()
        {
            var competingDrainHasRun = false;
            InMemoryBrokeredMessageOutbox racedOutbox = null;
            OutboxProcessor competingDrain = null;
            OutboxMessage racedRow = null;
            racedOutbox = CreateOutboxRacedBeforeEachClaim(_ =>
            {
                if (competingDrainHasRun)
                {
                    return;
                }
                competingDrainHasRun = true;
                DrainOneRowToCompletionAndElapseItsWait(competingDrain, racedOutbox, racedRow);
            });
            competingDrain = CreateDrainOver(racedOutbox);
            var racingDrain = CreateDrainOver(racedOutbox);
            racedRow = await EnqueueAndPollOneRow(racedOutbox);
            _dispatcher.FailureToThrow = new InvalidOperationException("the broker publish failed deliberately");

            await racingDrain.Process(racedRow);

            competingDrainHasRun.Should().BeTrue();
            _dispatcher.DispatchAttempts.Should().Be(1);
            _dispatcher.DeliveredMessageIds.Should().BeEmpty();
            racedRow.DispatchAttempts.Should().Be(1);
            racedRow.ProcessedFromOutboxAtUtc.Should().BeNull();
            (await ((IPollableOutboxStore)racedOutbox).GetUnprocessedMessagesFromOutbox()).Should().ContainSingle();
        }

        /// <summary>
        /// Builds the store the two race facts share: one whose claim seam fires before every compare-and-set, so a
        /// callback that disarms itself runs a competing drain in the window between one drain's observation and
        /// its claim.
        /// </summary>
        private InMemoryBrokeredMessageOutbox CreateOutboxRacedBeforeEachClaim(Action<string> beforeTakingTheDrainClaim)
            => new InMemoryBrokeredMessageOutbox(New.Common().RecordingLogger<InMemoryBrokeredMessageOutbox>().Creation,
                                                 new ReliabilityOptions(),
                                                 beforeTakingTheDrainClaim);

        // Runs a second drain end to end, synchronously, over the store's own copy of the row. It is called from
        // inside the first drain's claim path, so everything it does completes before that path resumes - which is
        // what makes the interleaving deterministic without threads, waits or a second task.
        private static void DrainOneRowToCompletion(OutboxProcessor drain, InMemoryBrokeredMessageOutbox outbox)
        {
            var row = outbox.GetUnprocessedMessagesFromOutbox().GetAwaiter().GetResult().Single();
            drain.Process(row).GetAwaiter().GetResult();
        }

        // As above, and then elapses the wait the competing drain's failed publish scheduled, so the row is due
        // again by the time the first drain's claim resumes. The row is passed in rather than polled back because
        // the poll is due-gated and would hand back nothing while that wait stands.
        private static void DrainOneRowToCompletionAndElapseItsWait(OutboxProcessor drain, InMemoryBrokeredMessageOutbox outbox, OutboxMessage row)
        {
            DrainOneRowToCompletion(drain, outbox);
            row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
        }

        /// <summary>
        /// The messaging infrastructure the drain publishes to, recording what it was handed. This is the delivery
        /// sink the facts read; <see cref="FailureToThrow"/> makes the publish fail the way a broker outage does.
        /// </summary>
        private sealed class RecordingInfrastructureDispatcher : IMessagingInfrastructureDispatcher
        {
            private readonly List<string> _deliveredMessageIds = new List<string>();

            public IReadOnlyList<string> DeliveredMessageIds => _deliveredMessageIds;
            public int DispatchAttempts { get; private set; }
            public Exception FailureToThrow { get; set; }

            public Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
                => throw new NotSupportedException("The outbox drain publishes one row per call and never uses the batch overload.");

            public Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
            {
                DispatchAttempts++;

                if (FailureToThrow != null)
                {
                    return Task.FromException(FailureToThrow);
                }

                _deliveredMessageIds.Add(brokeredMessage.MessageId);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Registers <see cref="RecordingInfrastructureDispatcher"/> under the Messaging Infrastructure type the
        /// persisted row names, so the REAL <see cref="MessagingInfrastructureProvider"/> performs the lookup the
        /// drain performs. The receive and path-building members are unreachable from a drain and say so rather
        /// than answering null.
        /// </summary>
        private sealed class DispatchOnlyInfrastructure : IMessagingInfrastructure
        {
            public DispatchOnlyInfrastructure(string type, IMessagingInfrastructureDispatcher dispatchInfrastructure)
            {
                Type = type;
                DispatchInfrastructure = dispatchInfrastructure;
            }

            public string Type { get; }
            public IMessagingInfrastructureDispatcher DispatchInfrastructure { get; }
            public IMessagingInfrastructureReceiver ReceiveInfrastructure => throw new NotSupportedException("The outbox drain never receives.");
            public IBrokeredMessagePathBuilder PathBuilder => throw new NotSupportedException("The outbox drain publishes to the persisted destination and builds no path.");
        }
    }
}
