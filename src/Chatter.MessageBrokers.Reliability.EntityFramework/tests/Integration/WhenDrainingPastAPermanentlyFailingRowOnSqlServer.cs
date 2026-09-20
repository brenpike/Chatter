using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Integration
{
    // CRITERION: the durable attempt state does over a REAL SQL Server database what the unit suites pin in
    // isolation - it stops a message whose dispatch keeps failing from holding its place at the head of every Outbox
    // Poll Batch, and it stops that same message from being published twice inside one drain. Both claims are made
    // over the PRODUCTION model (OutboxMessageConfiguration), through the REAL BrokeredMessageOutbox poll and the
    // REAL OutboxProcessor, with the messaging infrastructure as the only test double, so what is measured is what a
    // host running this package gets.
    //
    // WHY A PERMANENTLY-FAILING MESSAGE IS ONE THE BROKER REFUSES: the failure has to recur deterministically, and it
    // has to REACH the dispatcher, or the duplicate-publication claim would have nothing to count. A message the
    // broker refuses by destination satisfies both; the data-intrinsic sources (malformed persisted MessageContext, a
    // missing content type) fail BEFORE the dispatch and so can never duplicate a publish.
    //
    // WHY THE POLL SEQUENCE IS WRITTEN OUT HERE: BrokeredMessageOutboxProcessor is internal to Chatter.MessageBrokers
    // and this assembly is not in its InternalsVisibleTo list, so the drain loop itself cannot be driven from here.
    // The loop's own stop rule is pinned by UsingBrokeredMessageOutboxProcessor.WhenSendingOutboxMessages; what this
    // class replays is its observable shape - one FRESH store per poll, every polled message handed to the Outbox
    // Processor oldest first - over the database. Two polls is the whole drain for this backlog: the second answers
    // with fewer than a full Outbox Poll Batch, which is where that loop takes its interval wait.
    [Trait("Category", "Integration")]
    [Collection(EfReliabilitySqlServerCollection.Name)]
    public class WhenDrainingPastAPermanentlyFailingRowOnSqlServer : Testing.Core.Context
    {
        // The Outbox Poll Batch is filled ENTIRELY by messages the broker refuses, which is the wedge this step
        // closes: at as few as this many of them, nothing behind them could be polled at all.
        private const int OutboxPollBatchSize = 3;
        private const string Infrastructure = "test-infrastructure";
        private const string RefusedDestination = "destination-the-broker-refuses";
        private const string DeliverableDestination = "destination-the-broker-accepts";
        private const string MessageBody = "payload";
        private const string JsonContentType = "application/json";

        private readonly EfReliabilitySqlServerFixture _fixture;
        private readonly RefusingInfrastructureDispatcher _dispatcher = new RefusingInfrastructureDispatcher { RefusedDestination = RefusedDestination };
        private readonly ReliabilityOptions _options = CreateOptions();
        private readonly List<IReadOnlyList<string>> _polls = new List<IReadOnlyList<string>>();

        private SqlServerOutboxContextHarness _harness;
        private IReadOnlyList<string> _refusedMessageIds;
        private string _deliverableMessageId;
        private DateTime _drainStartedAtUtc;
        private DateTime _drainEndedAtUtc;

        public WhenDrainingPastAPermanentlyFailingRowOnSqlServer(EfReliabilitySqlServerFixture fixture)
            => _fixture = fixture;

        // CLAIM 1 - STARVATION IS CLOSED. A full Outbox Poll Batch of messages the broker refuses does not stop the
        // message behind them from being selected and published. The sinks are the store's own second poll and the
        // messages the infrastructure dispatcher was actually handed, plus the row's processed stamp read back
        // through a fresh context - never the drain's own condition.
        // MEASURED: removing the due clause from the poll - the selection this package shipped before this work -
        // reddens this fact, and the drain's second poll then comes back with the same three refused message ids
        // rather than the message behind them. That mutation reddens all four facts here plus the four due-gate
        // facts in WhenGettingUnprocessedMessages. Scheduling a failed dispatch at now rather than a backoff ahead
        // reddens all four facts here and NOTHING else in this project (both observed).
        [RequiresDockerFact]
        public async Task MustDispatchTheMessageBehindAFullBatchOfPermanentlyFailingMessages()
        {
            await DrainTheSeededBacklogAsync();

            _polls[0].Should().BeEquivalentTo(_refusedMessageIds,
                "the first Outbox Poll Batch is filled by the messages the broker refuses, so the message behind them cannot be taken yet");
            _polls[1].Should().ContainSingle().Which.Should().Be(_deliverableMessageId,
                "the refused messages are scheduled a backoff ahead, so the next poll's batch is filled from behind them");
            _dispatcher.DeliveredMessageIds.Should().ContainSingle().Which.Should().Be(_deliverableMessageId);

            using var verifyContext = _harness.CreateContext();
            var delivered = await verifyContext.Set<OutboxMessage>().SingleAsync(message => message.MessageId == _deliverableMessageId);
            delivered.ProcessedFromOutboxAtUtc.Should().NotBeNull("a published message is claimed, so no later poll republishes it");
        }

        // CLAIM 2 - DUPLICATE DISPATCH IS CLOSED BY THE SAME CHANGE. Every message reaches the broker exactly once
        // during the drain: the refused ones are held back by the due gate the attempt they just spent scheduled, so
        // the drain's second poll cannot hand them over again. Equivalence is the assertion because it counts
        // repeats - a second publication of any refused message id fails it.
        // MEASURED: under the pre-change selection the dispatcher is handed SIX messages - every refused id twice and
        // the message behind them never - so this and the fact above go red together. NO mutation reddened one of the
        // two alone: the selection change that closes the starvation is the same change that closes the within-drain
        // duplication, which is why this is a fact of its own rather than a second assertion on that one.
        [RequiresDockerFact]
        public async Task MustDispatchEachPermanentlyFailingMessageExactlyOnceInOneDrain()
        {
            await DrainTheSeededBacklogAsync();

            _dispatcher.DispatchedMessageIds.Should().BeEquivalentTo(_refusedMessageIds.Append(_deliverableMessageId),
                "one drain publishes each message once - the refused ones on its first poll and the message behind them on its second");
        }

        // THE ATTEMPT STATE IS DURABLE. The two columns are read back through a FRESH context, so what is asserted is
        // what SQL Server stores rather than what the change tracker remembers - the poll that holds these messages
        // back is a later poll in another scope and can read nothing else. The schedule is bounded on BOTH sides by
        // the instants the drain ran between, so neither a message left due now nor one parked at an instant no poll
        // ever reaches satisfies it.
        // MEASURED: dropping the increment from the attempt write - setting DispatchAttempts to itself - reddens this
        // fact and no other fact in this class, plus the three attempt-count facts in WhenUpdatingProcessed that pin
        // the same write (observed).
        [RequiresDockerFact]
        public async Task MustCarryTheAttemptStateOfEveryRefusedMessageInTheDatabase()
        {
            await DrainTheSeededBacklogAsync();

            using var verifyContext = _harness.CreateContext();
            var refused = await verifyContext.Set<OutboxMessage>()
                                             .Where(message => message.Destination == RefusedDestination)
                                             .ToListAsync();

            refused.Should().HaveCount(OutboxPollBatchSize);
            foreach (var message in refused)
            {
                message.ProcessedFromOutboxAtUtc.Should().BeNull("a publish that never reached the broker leaves the message unclaimed");
                message.DispatchAttempts.Should().Be(1);
                message.NextAttemptAtUtc.Should()
                       .BeOnOrAfter(_drainStartedAtUtc.AddSeconds(_options.OutboxDispatchBackoffBaseInSeconds))
                       .And.BeOnOrBefore(_drainEndedAtUtc.AddSeconds(_options.OutboxDispatchBackoffBaseInSeconds));
            }
        }

        // THE CLAIM PATH IS UNDISTURBED BY THE ATTEMPT WRITE. RecordDispatchAttempt writes through ExecuteUpdateAsync,
        // which bypasses the change tracker AND the ProcessedFromOutboxAtUtc concurrency token; this is the fact that
        // the row it wrote is still claimable the ordinary way once its wait has elapsed. Elapsing the wait stands in
        // for wall-clock time and touches nothing but the instant.
        // NOTE: it deliberately asserts nothing about the attempt COUNT - that is the fact above - so the two fail
        // for different reasons.
        // MEASURED: NO mutation reddened this fact alone. Making the attempt write also stamp
        // ProcessedFromOutboxAtUtc - a write that genuinely fights the claim - reddens this together with the
        // attempt-state fact above and with
        // WhenUpdatingProcessed.MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed (observed). It
        // is a REGRESSION guard on the claim path rather than an exclusive oracle, and it is recorded as one.
        [RequiresDockerFact]
        public async Task MustClaimAMessageWhoseAttemptStateWasWrittenOutsideTheChangeTracker()
        {
            await DrainTheSeededBacklogAsync();
            var retriedMessageId = _refusedMessageIds[0];
            await ElapseTheScheduledWaitAsync(retriedMessageId);
            _dispatcher.RefusedDestination = null;

            var retryPoll = await PollAndProcessOnceAsync();

            retryPoll.Should().ContainSingle().Which.Should().Be(retriedMessageId,
                "only the message whose wait elapsed is due; its siblings are still held back");
            _dispatcher.DeliveredMessageIds.Should().Contain(retriedMessageId);

            using var verifyContext = _harness.CreateContext();
            var claimed = await verifyContext.Set<OutboxMessage>().SingleAsync(message => message.MessageId == retriedMessageId);
            claimed.ProcessedFromOutboxAtUtc.Should().NotBeNull("the tracked claim still matches a row whose attempt state was written outside the tracker");
        }

        private async Task DrainTheSeededBacklogAsync()
        {
            await SeedBacklogAsync();

            _drainStartedAtUtc = DateTime.UtcNow;
            _polls.Add(await PollAndProcessOnceAsync());
            _polls.Add(await PollAndProcessOnceAsync());
            _drainEndedAtUtc = DateTime.UtcNow;
        }

        // Seeds one Outbox Poll Batch of messages the broker refuses, all staged BEFORE the one it accepts, so the
        // message that must not starve is genuinely behind them under the poll's oldest-first ordering.
        private async Task SeedBacklogAsync()
        {
            var connectionString = await _fixture.CreateDatabaseAsync("ef_outbox_drain");
            _harness = SqlServerOutboxContextHarness.Create(connectionString);

            var stagedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var refused = Enumerable.Range(0, OutboxPollBatchSize)
                                    .Select(position => CreateStagedMessage(RefusedDestination, stagedAtUtc.AddMinutes(position)))
                                    .ToList();
            var deliverable = CreateStagedMessage(DeliverableDestination, stagedAtUtc.AddMinutes(OutboxPollBatchSize));

            using var seedContext = _harness.CreateContext();
            await seedContext.Set<OutboxMessage>().AddRangeAsync(refused);
            await seedContext.Set<OutboxMessage>().AddAsync(deliverable);
            await seedContext.SaveChangesAsync();

            _refusedMessageIds = refused.Select(message => message.MessageId).ToList();
            _deliverableMessageId = deliverable.MessageId;
        }

        // ONE poll in its own store over its own context, mirroring the fresh scope the drain opens per poll, with
        // every polled message handed to the Outbox Processor oldest first the way the drain hands them over.
        private async Task<IReadOnlyList<string>> PollAndProcessOnceAsync()
        {
            using var context = _harness.CreateContext();
            var outbox = new BrokeredMessageOutbox<SqlServerOutboxContext>(context, CreateLoggerFactory(), _options);
            var processor = new OutboxProcessor(CreateInfrastructureProvider(),
                                                New.Common().RecordingLogger<OutboxProcessor>().Creation,
                                                new BodyConverterFactory(new IBrokeredMessageBodyConverter[] { new JsonBodyConverter() }),
                                                outbox,
                                                _options);

            var batch = (await ((IPollableOutboxStore)outbox).GetUnprocessedMessagesFromOutbox()).ToList();
            foreach (var message in batch.OrderBy(message => message.SentToOutboxAtUtc))
            {
                await processor.Process(message);
            }

            return batch.Select(message => message.MessageId).ToList();
        }

        private async Task ElapseTheScheduledWaitAsync(string messageId)
        {
            using var context = _harness.CreateContext();
            var message = await context.Set<OutboxMessage>().SingleAsync(candidate => candidate.MessageId == messageId);
            message.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(-1);
            await context.SaveChangesAsync();
        }

        private OutboxMessage CreateStagedMessage(string destination, DateTime sentToOutboxAtUtc)
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.Destination = destination;
            message.SentToOutboxAtUtc = sentToOutboxAtUtc;
            message.MessageContentType = JsonContentType;
            message.MessageBody = MessageBody;
            message.MessageContext = ChatterJson.Serialize(new Dictionary<string, object> { [MessageContext.InfrastructureType] = Infrastructure });
            return message;
        }

        private IMessagingInfrastructureProvider CreateInfrastructureProvider()
            => new MessagingInfrastructureProvider(new IMessagingInfrastructure[] { new DispatchOnlyInfrastructure(Infrastructure, _dispatcher) },
                                                   New.Common().RecordingLogger<MessagingInfrastructureProvider>().Creation);

        // ReliabilityOptions.OutboxPollBatchSize has an internal setter and ReliabilityOptionsBuilder refuses a value
        // this small, so the only way to name the batch this fixture needs is to set the property directly.
        private static ReliabilityOptions CreateOptions()
        {
            var options = new ReliabilityOptions();
            typeof(ReliabilityOptions).GetProperty(nameof(ReliabilityOptions.OutboxPollBatchSize)).SetValue(options, OutboxPollBatchSize);
            return options;
        }

        private static ILoggerFactory CreateLoggerFactory()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            return loggerFactory.Object;
        }

        /// <summary>
        /// The messaging infrastructure the drain publishes to. It records every message id it was handed - the sink
        /// the duplicate-publication fact counts - and refuses the destination the permanently-failing messages carry,
        /// which is a failure that recurs for as long as that destination is unroutable.
        /// </summary>
        private sealed class RefusingInfrastructureDispatcher : IMessagingInfrastructureDispatcher
        {
            private readonly List<string> _dispatchedMessageIds = new List<string>();
            private readonly List<string> _deliveredMessageIds = new List<string>();

            public string RefusedDestination { get; set; }
            public IReadOnlyList<string> DispatchedMessageIds => _dispatchedMessageIds;
            public IReadOnlyList<string> DeliveredMessageIds => _deliveredMessageIds;

            public Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
                => throw new NotSupportedException("The outbox drain publishes one message per call and never uses the batch overload.");

            public Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
            {
                _dispatchedMessageIds.Add(brokeredMessage.MessageId);

                if (brokeredMessage.Destination == RefusedDestination)
                {
                    return Task.FromException(new InvalidOperationException($"The broker refuses destination '{brokeredMessage.Destination}'."));
                }

                _deliveredMessageIds.Add(brokeredMessage.MessageId);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Registers <see cref="RefusingInfrastructureDispatcher"/> under the Messaging Infrastructure type the
        /// persisted message names, so the REAL <see cref="MessagingInfrastructureProvider"/> performs the lookup the
        /// drain performs. The receive and path-building members are unreachable from a drain and say so rather than
        /// answering null.
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
