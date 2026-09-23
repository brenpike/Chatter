using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    // CRITERION: the re-claim the drain makes after a publish whose claim failed reaches the right end state over a
    // REAL relational store - one whose change tracker still carries the claim the rolled-back unit of work staged.
    // Every other fact covering this exit drives a MOCKED outbox, and a mock has no change tracker, so none of them
    // can tell a claim this path issued from residue a later save happened to flush. That is the whole reason the
    // path was rewritten, and it is what this class measures: the real OutboxProcessor over the real
    // BrokeredMessageOutbox over SQLite, with the messaging infrastructure and the claim's failure as the only test
    // doubles.
    //
    // BOUND: SqliteOutboxContext maps no concurrency token on ProcessedFromOutboxAtUtc, so the claim issued here
    // carries no 'still unprocessed' predicate the way the production OutboxMessageConfiguration makes it carry one.
    // That token is pinned over a real database by Integration/WhenClaimingOutboxConcurrentlyOnSqlServer. What these
    // facts measure is the change-tracker residue a rolled-back claim leaves, which is there either way.
    public class WhenReclaimingAfterAFailedClaimOverSqlite : Testing.Core.Context, IAsyncDisposable
    {
        private const string Infrastructure = "test-infrastructure";
        private const string Destination = "destination-the-broker-accepts";
        private const string MessageBody = "payload";
        private const string JsonContentType = "application/json";

        private readonly SqliteOutboxContextHarness _harness = SqliteOutboxContextHarness.Create();
        private readonly FaultingClaimSaveInterceptor _faultingClaim = new FaultingClaimSaveInterceptor();
        private readonly Mock<IMessagingInfrastructureDispatcher> _dispatcher = new Mock<IMessagingInfrastructureDispatcher>();

        public WhenReclaimingAfterAFailedClaimOverSqlite()
            => _dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                          .Returns(Task.CompletedTask);

        // THE ROW ENDS PROCESSED AND NO ATTEMPT IS SPENT. The message reaches the broker, the claim that follows it
        // is faulted with a failure that is not a concurrency conflict, and the drain re-claims. The end state is
        // read back through a FRESH context, so what is asserted is what SQLite stores rather than what the change
        // tracker still carries - a later poll in another scope can read nothing else. The three modifying
        // statements counted are the DRAIN CLAIM this drain was granted, the claim that failed and the re-claim
        // that replaced it; a fourth would mean another write reached the row.
        // MEASURED, the re-claim: deleting the re-claim call from OutboxProcessor's failure catch reddens this fact
        // and no other in this project (241 of 242 passed), with:
        //   Expected stored.ProcessedFromOutboxAtUtc to have a value ..., but found <null>.
        // MEASURED, the count: issuing the re-claim's unit of work TWICE reddens this fact and no other in this
        // project (241 of 242 passed), with:
        //   Expected _faultingClaim.ObservedModificationCount to be 3 ..., but found 4.
        // Every assertion ABOVE the count passes under that second mutation, and that is exactly what the count is
        // here to catch: a write that reaches the row leaving every column reading as it should, so a fact
        // asserting only the stored columns cannot see it. This one counts the statements that produced them.
        [Fact]
        public async Task MustLeaveTheRowProcessedAndSpendNoAttemptWhenTheClaimFailsAfterAPublish()
        {
            var staged = SeedStagedMessage();
            using var context = CreateDrainingContext();
            var outbox = new BrokeredMessageOutbox<SqliteOutboxContext>(context, CreateLoggerFactory());
            var processor = CreateProcessor(outbox);
            var polled = (await ((IPollableOutboxStore)outbox).GetUnprocessedMessagesFromOutbox()).Single();

            await processor.Process(polled);

            var stored = ReadStoredMessage(staged.MessageId);
            stored.ProcessedFromOutboxAtUtc.Should().NotBeNull(
                "the message is on the broker, so the row it came from must never be published again");
            stored.DispatchAttempts.Should().Be(0,
                "a delivered message spends no attempt - the claim failed, the dispatch did not");
            stored.NextAttemptAtUtc.Should().Be(polled.NextAttemptAtUtc,
                "the row keeps the instant the DRAIN CLAIM wrote, not one a failure scheduled");

            _dispatcher.Verify(d => d.Dispatch(It.Is<OutboundBrokeredMessage>(published => published.MessageId == staged.MessageId), null), Times.Once);
            _faultingClaim.InjectedFailureCount.Should().Be(1,
                "the fact proves nothing unless the claim genuinely failed");
            _faultingClaim.ObservedModificationCount.Should().Be(3,
                "the drain claim, the claim that failed and the re-claim that replaced it are the only writes this drain makes");
        }

        // THE FIXTURE'S OWN PREMISE. A DbUpdateConcurrencyException would take BrokeredMessageOutbox's compensation
        // path, which resyncs the conflicted entry and resets it to Unchanged - leaving no residue at all, and so a
        // different exit from the one the fact above drives. This pins that the injected fault is not one, so that
        // fact cannot pass by exercising compensation instead.
        // The assertion is written against the base exception rather than the exception surfaced, because whether a
        // provider wraps a statement's failure on the way out is the provider's business and not a claim this
        // project makes.
        [Fact]
        public async Task MustFailTheClaimWithAFaultThatIsNotAConcurrencyConflict()
        {
            SeedStagedMessage();
            using var context = CreateDrainingContext();
            var outbox = new BrokeredMessageOutbox<SqliteOutboxContext>(context, CreateLoggerFactory());
            var pollable = (IPollableOutboxStore)outbox;
            var polled = (await pollable.GetUnprocessedMessagesFromOutbox()).Single();

            Func<Task> claim = () => ((IUnitOfWork)outbox).ExecuteAsync(ct => pollable.UpdateProcessedDate(polled, ct), null);

            var thrown = await claim.Should().ThrowAsync<Exception>();
            thrown.Which.Should().NotBeAssignableTo<DbUpdateConcurrencyException>(
                "a concurrency conflict is compensated for and leaves no residue, which is not the exit under test");
            thrown.Which.GetBaseException().Should().BeOfType<ClaimSaveFaultException>(
                "the claim must fail for the reason the fixture injected and no other");
            _faultingClaim.InjectedFailureCount.Should().Be(1);
        }

        private OutboxMessage SeedStagedMessage()
        {
            OutboxMessage message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            message.Id = 1;
            message.Destination = Destination;
            message.MessageContentType = JsonContentType;
            message.MessageBody = MessageBody;
            message.MessageContext = ChatterJson.Serialize(new Dictionary<string, object> { [MessageContext.InfrastructureType] = Infrastructure });

            using var seedContext = _harness.CreateContext();
            seedContext.Add(message);
            seedContext.SaveChanges();

            return message;
        }

        private SqliteOutboxContext CreateDrainingContext()
            => _harness.CreateContext(options => options.AddInterceptors(_faultingClaim));

        private OutboxProcessor CreateProcessor(BrokeredMessageOutbox<SqliteOutboxContext> outbox)
            => new OutboxProcessor(CreateInfrastructureProvider(),
                                   New.Common().RecordingLogger<OutboxProcessor>().Creation,
                                   new BodyConverterFactory(new IBrokeredMessageBodyConverter[] { new JsonBodyConverter() }),
                                   outbox);

        private IMessagingInfrastructureProvider CreateInfrastructureProvider()
        {
            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(Infrastructure)).Returns(_dispatcher.Object);
            return provider.Object;
        }

        private OutboxMessage ReadStoredMessage(string messageId)
        {
            using var context = _harness.CreateContext();

            return context.Set<OutboxMessage>().AsNoTracking().Single(stored => stored.MessageId == messageId);
        }

        private static ILoggerFactory CreateLoggerFactory()
        {
            var loggerFactory = new Mock<ILoggerFactory>();
            loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            return loggerFactory.Object;
        }

        public async ValueTask DisposeAsync() => await _harness.DisposeAsync();
    }
}
