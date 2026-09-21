using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageInbox
{
    public class WhenReceivingViaInbox : Testing.Core.Context
    {
        private DbContextCreator _context;
        private readonly DbContext _dbContext;
        private readonly BrokeredMessageInbox<DbContext> _sut;
        private readonly LoggerCreator<BrokeredMessageInbox<DbContext>> _loggerCreator;
        private readonly ILogger<BrokeredMessageInbox<DbContext>> _logger;
        private readonly ReliabilityOptions _options;

        public WhenReceivingViaInbox()
        {
            _context = New.MessageBrokers().DbContext();
            _dbContext = _context;
            _loggerCreator = New.Common().Logger<BrokeredMessageInbox<DbContext>>();
            _logger = _loggerCreator.Creation;
            _options = new ReliabilityOptions();
            _sut = new BrokeredMessageInbox<DbContext>(_context, _logger, _options);
        }

        private BrokeredMessageInbox<DbContext> CreateSutWithDeduplicationWindow(TimeSpan? deduplicationWindow)
            => new BrokeredMessageInbox<DbContext>(_context,
                                                   _logger,
                                                   _options,
                                                   new EntityFrameworkReliabilityOptions { InboxDeduplicationWindow = deduplicationWindow });

        private InboxMessage GivenAMarker(string messageId, DateTime? receivedAtUtc)
        {
            var marker = new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = receivedAtUtc };
            _dbContext.Add(marker);
            _dbContext.SaveChanges();

            return marker;
        }

        private static BrokeredMessageInbox<InboxClaimSqliteContext> CreateSqliteSut(InboxClaimSqliteContext context, TimeSpan? deduplicationWindow = null)
            => new BrokeredMessageInbox<InboxClaimSqliteContext>(context,
                                                                 NullLogger<BrokeredMessageInbox<InboxClaimSqliteContext>>.Instance,
                                                                 new ReliabilityOptions(),
                                                                 new EntityFrameworkReliabilityOptions { InboxDeduplicationWindow = deduplicationWindow });

        // Reads through a SECOND context enlisted in the owner's open transaction. Microsoft.Data.Sqlite refuses a
        // command whose transaction is not the connection's pending one, so the enlistment is what makes an
        // uncommitted claim readable at all; and reading through a second context is what makes the result a
        // flushed row rather than the owner's own change-tracker entry.
        private static InboxClaimSqliteContext CreateReaderEnlistedIn(InboxClaimSqliteHarness harness, InboxClaimSqliteContext owner)
        {
            var reader = harness.CreateContext();
            reader.Database.UseTransaction(owner.Database.CurrentTransaction.GetDbTransaction());

            return reader;
        }

        private static async Task GivenACommittedMarkerAsync(InboxClaimSqliteHarness harness, string messageId, DateTime? receivedAtUtc)
        {
            using var seedContext = harness.CreateContext();
            seedContext.Set<InboxMessage>().Add(new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = receivedAtUtc });
            await seedContext.SaveChangesAsync();
        }

        private static IMessageBrokerContext CreateContext(string messageId, CancellationToken cancellationToken = default)
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.Setup(c => c.ContentType).Returns("application/json");

            return new MessageBrokerContext(
                messageId,
                Array.Empty<byte>(),
                new Dictionary<string, object>(),
                "test-receiver",
                cancellationToken,
                converter.Object);
        }

        // INVARIANT: the claim on a fresh message id reaches the store BEFORE the handler runs. Oracle for the
        // ordering inside a single delivery; moving the flush in TryClaimMessageIdAsync to after the handler
        // reddens this fact. See
        // docs/adr/0033-the-relational-inbox-claims-the-message-id-before-the-handler-inside-the-ambient-transaction.md.
        [Fact]
        public async Task MustRecordTheClaimBeforeInvokingTheHandlerForAFreshMessageId()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            using var context = harness.CreateContext();
            var sut = CreateSqliteSut(context);
            var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);
            var messageId = Guid.NewGuid().ToString();

            var claimVisibleToTheHandler = false;
            var handlerInvoked = false;

            await unitOfWork.ExecuteAsync(
                _ => sut.ReceiveViaInbox("payload", CreateContext(messageId), async () =>
                {
                    using var reader = CreateReaderEnlistedIn(harness, context);
                    claimVisibleToTheHandler = await reader.Set<InboxMessage>().AnyAsync(m => m.MessageId == messageId);
                    handlerInvoked = true;
                }),
                null);

            claimVisibleToTheHandler.Should().BeTrue("the message id must be claimed in the store before the handler is invoked");
            handlerInvoked.Should().BeTrue();
        }

        // INVARIANT: the inbox flushes its claim into the ambient transaction and commits nothing, leaving the
        // unit of work's single commit as the only one. Oracle for a fresh message id; committing the ambient
        // transaction after the flush in TryClaimMessageIdAsync reddens this fact. The commit count is asserted
        // zero and THEN observed rising, so an interceptor that was never wired up cannot pass for "never
        // committed".
        [Fact]
        public async Task MustFlushTheClaimWithoutCommittingForAFreshMessageId()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var commitCounter = new InboxCommitCountingTransactionInterceptor();
            using var context = harness.CreateContext(options => options.AddInterceptors(commitCounter));
            var sut = CreateSqliteSut(context);
            var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);
            var messageId = Guid.NewGuid().ToString();

            await unitOfWork.ExecuteAsync(async _ =>
            {
                await sut.ReceiveViaInbox("payload", CreateContext(messageId), () => Task.CompletedTask);

                using (var reader = CreateReaderEnlistedIn(harness, context))
                {
                    (await reader.Set<InboxMessage>().AnyAsync(m => m.MessageId == messageId))
                        .Should().BeTrue("the claim must have been flushed into the ambient transaction");
                }

                commitCounter.CommitCount.Should().Be(0, "the inbox must leave the commit to the unit of work");
            }, null);

            commitCounter.CommitCount.Should().Be(1, "the counter must see a commit when one happens, or the zero above proves nothing");
        }

        // INVARIANT: the same flush-without-commit holds when the claim refreshes an expired marker in place
        // rather than inserting a fresh one, which is a different statement down a different branch.
        [Fact]
        public async Task MustFlushTheRefreshedClaimWithoutCommittingForAnExpiredMessageId()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var messageId = Guid.NewGuid().ToString();
            var staleReceivedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            await GivenACommittedMarkerAsync(harness, messageId, staleReceivedAtUtc);

            var commitCounter = new InboxCommitCountingTransactionInterceptor();
            using var context = harness.CreateContext(options => options.AddInterceptors(commitCounter));
            var sut = CreateSqliteSut(context, TimeSpan.FromMinutes(1));
            var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

            await unitOfWork.ExecuteAsync(async _ =>
            {
                await sut.ReceiveViaInbox("payload", CreateContext(messageId), () => Task.CompletedTask);

                using (var reader = CreateReaderEnlistedIn(harness, context))
                {
                    var refreshed = await reader.Set<InboxMessage>()
                        .Where(m => m.MessageId == messageId)
                        .Select(m => m.ReceivedByInboxAtUtc)
                        .SingleAsync();
                    refreshed.Should().BeAfter(staleReceivedAtUtc, "the refreshed claim must have been flushed into the ambient transaction");
                }

                commitCounter.CommitCount.Should().Be(0, "the inbox must leave the commit to the unit of work");
            }, null);

            commitCounter.CommitCount.Should().Be(1, "the counter must see a commit when one happens, or the zero above proves nothing");
        }

        // INVARIANT: the inbox refuses to claim a message id when its context carries no transaction, and refuses
        // before staging anything. A flush outside a transaction autocommits, so a failure between that autocommit
        // and the handler's work would leave a marker suppressing a message nothing ever handled. Oracle for the
        // refusal; deleting the CurrentTransaction guard in TryClaimMessageIdAsync reddens this fact.
        [Fact]
        public async Task MustRefuseToClaimOutsideATransaction()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            using var context = harness.CreateContext();
            var sut = CreateSqliteSut(context);
            var messageId = Guid.NewGuid().ToString();
            var handlerInvoked = false;

            Func<Task> act = () => sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            var refusal = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
            refusal.Message.Should().Contain("WithInboxBehavior", "the refusal must name the registration that fixes it");
            refusal.Message.Should().Contain("has no active transaction",
                "this fact must name the NO-TRANSACTION refusal specifically, or the ownership refusal satisfies it too and deleting the CurrentTransaction guard reddens nothing");

            handlerInvoked.Should().BeFalse("the refusal must come before the handler");
            context.ChangeTracker.Entries<InboxMessage>().Should().BeEmpty("the refusal must come before anything is staged");

            using var verifyContext = harness.CreateContext();
            (await verifyContext.Set<InboxMessage>().ToListAsync()).Should().BeEmpty();
        }

        // INVARIANT: the inbox refuses to claim a message id inside a transaction no unit of work began, and
        // refuses before staging anything. Every protection that undoes a claim whose handler did not return - the
        // change-tracker reconciliation on UnitOfWork's rollback, and PersistanceTransaction's unsettled-claim
        // refusal - acts only on a transaction a unit of work owns, so a claim flushed into a caller's transaction
        // is left in this context's identity map when that caller rolls back, and the next lookup finds it and
        // skips a message nothing handled. Oracle for the OWNERSHIP refusal; deleting the ownership guard in
        // TryClaimMessageIdAsync, or the registration in UnitOfWork.BeginAsync's begun-here branch, reddens this
        // fact. It does NOT pin the grant - a guard that refused every claim passes here - which
        // MustCommitTheClaimWhenTheHandlerReturns pins instead. The message assertion names a phrase unique to
        // ownership so the no-transaction refusal cannot satisfy this fact if the two guards are ever merged. See
        // docs/adr/0033-the-relational-inbox-claims-the-message-id-before-the-handler-inside-the-ambient-transaction.md
        // and docs/adr/0035-a-rolled-back-unit-of-work-reconciles-its-contexts-change-tracker.md.
        [Fact]
        public async Task MustRefuseToClaimInsideATransactionNoUnitOfWorkBegan()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            using var context = harness.CreateContext();
            var sut = CreateSqliteSut(context);
            var messageId = Guid.NewGuid().ToString();
            var handlerInvoked = false;

            using var callerTransaction = await context.Database.BeginTransactionAsync();

            Func<Task> act = () => sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("no unit of work owns",
                    "the refusal must name OWNERSHIP, so the no-transaction refusal cannot pass for this one");

            handlerInvoked.Should().BeFalse("the refusal must come before the handler");
            context.ChangeTracker.Entries<InboxMessage>().Should().BeEmpty("the refusal must come before anything is staged");
        }

        [Fact]
        public async Task MustNotInvokeHandlerOrAddSecondRowForDuplicateMessageId()
        {
            var messageId = Guid.NewGuid().ToString();
            _dbContext.Add(new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = DateTime.UtcNow });
            _dbContext.SaveChanges();

            var context = CreateContext(messageId);
            var handlerInvoked = false;

            await _sut.ReceiveViaInbox("payload", context, () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            handlerInvoked.Should().BeFalse();
            var persisted = await _dbContext.Set<InboxMessage>().ToListAsync();
            persisted.Should().HaveCount(1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustBypassInboxAndInvokeHandlerWhenMessageIdIsNullEmptyOrWhitespace(string messageId)
        {
            var context = CreateContext(messageId);
            var handlerInvoked = false;

            await _sut.ReceiveViaInbox("payload", context, () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            handlerInvoked.Should().BeTrue();
            var persisted = await _dbContext.Set<InboxMessage>().ToListAsync();
            persisted.Should().BeEmpty();
        }

        // INVARIANT: a claim flushed ahead of the handler is undone by the unit of work's rollback along with
        // everything else that handler touched, so a marker never outlives a handler that threw. Oracle for the
        // atomicity of claim and handler; committing the claim on its own reddens this fact.
        [Fact]
        public async Task MustPropagateHandlerExceptionAndNotPersistInboxMessage()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var messageId = Guid.NewGuid().ToString();
            var expected = new InvalidOperationException("handler failed");

            using (var context = harness.CreateContext())
            {
                var sut = CreateSqliteSut(context);
                var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

                Func<Task> act = () => unitOfWork.ExecuteAsync(
                    _ => sut.ReceiveViaInbox<string>("payload", CreateContext(messageId), () => throw expected),
                    null);

                (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(expected);
            }

            using var verifyContext = harness.CreateContext();
            (await verifyContext.Set<InboxMessage>().ToListAsync()).Should().BeEmpty();
        }

        // INVARIANT: a claim whose handler threw leaves nothing behind in the change tracker, so a redelivery over
        // that same context reads the store rather than the failed delivery's leftover entry. Oracle for the fresh
        // branch; deleting the change-tracker clear on UnitOfWork's owned rollback path reddens this fact. See
        // docs/adr/0033-the-relational-inbox-claims-the-message-id-before-the-handler-inside-the-ambient-transaction.md
        // and docs/adr/0035-a-rolled-back-unit-of-work-reconciles-its-contexts-change-tracker.md.
        [Fact]
        public async Task MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAFreshMessageId()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            using var context = harness.CreateContext();
            var sut = CreateSqliteSut(context);
            var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);
            var messageId = Guid.NewGuid().ToString();
            var expected = new InvalidOperationException("handler failed");

            Func<Task> act = () => unitOfWork.ExecuteAsync(
                _ => sut.ReceiveViaInbox<string>("payload", CreateContext(messageId), () => throw expected),
                null);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(expected);

            context.ChangeTracker.Entries<InboxMessage>()
                .Should().BeEmpty("a rolled back claim must not stay tracked for the next lookup to find");

            var handlerInvoked = false;

            await unitOfWork.ExecuteAsync(
                _ => sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
                {
                    handlerInvoked = true;
                    return Task.CompletedTask;
                }),
                null);

            handlerInvoked.Should().BeTrue("a message id nothing ever committed must still reach the handler");
        }

        // INVARIANT: the same holds when the claim refreshed an expired marker in place, where the leftover entry
        // would also carry a ReceivedByInboxAtUtc concurrency token no store row carries. Oracle for the expired
        // branch; deleting the change-tracker clear on UnitOfWork's owned rollback path reddens this fact.
        [Fact]
        public async Task MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAnExpiredMessageId()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var messageId = Guid.NewGuid().ToString();
            var staleReceivedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            await GivenACommittedMarkerAsync(harness, messageId, staleReceivedAtUtc);

            using var context = harness.CreateContext();
            var sut = CreateSqliteSut(context, TimeSpan.FromMinutes(1));
            var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);
            var expected = new InvalidOperationException("handler failed");

            Func<Task> act = () => unitOfWork.ExecuteAsync(
                _ => sut.ReceiveViaInbox<string>("payload", CreateContext(messageId), () => throw expected),
                null);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(expected);

            context.ChangeTracker.Entries<InboxMessage>()
                .Should().BeEmpty("a rolled back refresh must not stay tracked for the next lookup to find");

            var handlerInvoked = false;

            await unitOfWork.ExecuteAsync(
                _ => sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
                {
                    handlerInvoked = true;
                    return Task.CompletedTask;
                }),
                null);

            handlerInvoked.Should().BeTrue("the redelivery must read the store's stale marker, which the window ages out");
        }

        // INVARIANT: a claim whose flush failed on a COMPANION entity's write leaves nothing behind in the change
        // tracker, so a redelivery over that same context reads the store rather than the failed delivery's leftover
        // Added entry. Oracle for the rethrow exit of TryClaimMessageIdAsync, which returns through the throw above
        // its own detach; deleting the change-tracker clear on UnitOfWork's owned rollback path reddens this fact.
        //
        // The COMPANION is what makes the absorption gate fail, and it is load bearing rather than incidental. The
        // gate reads ex.Entries.Count == 1 && ReferenceEquals(ex.Entries[0].Entity, claim), which holds by
        // construction whenever the claim is the only failing entity, so faulting the claim's OWN write drives
        // ABSORPTION - detach, re-read, rethrow - and leaves the claim already gone from the tracker, which is a
        // different exit and not the one this fact pins. Staging a companion in the same change tracker, the way a
        // dispatch nested inside another handler does, is what puts a different entity in that entry.
        //
        // The CHANGE TRACKER and the SECOND DELIVERY REACHING ITS HANDLER are the discriminating observations. The
        // STORE is deliberately not asserted on: nothing commits on this path, so an empty store reads the same
        // whether the claim survived in the tracker or not. InjectedFailureCount pins that the hook fired at all,
        // without which an empty tracker reads identically to a flush that simply succeeded.
        [Fact]
        public async Task MustNotSuppressARedeliveryOverTheSameContextWhenACompanionWriteFailedTheClaimsFlush()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var messageId = Guid.NewGuid().ToString();
            var companionMessageId = Guid.NewGuid().ToString();
            var faultingCompanionWrite = new FaultingInboxClaimFlushInterceptor(companionMessageId);

            using var context = harness.CreateContext(options => options.AddInterceptors(faultingCompanionWrite));
            var sut = CreateSqliteSut(context);
            var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

            Func<Task> act = () => unitOfWork.ExecuteAsync(
                _ =>
                {
                    context.Set<InboxMessage>().Add(new InboxMessage { MessageId = companionMessageId, ReceivedByInboxAtUtc = DateTime.UtcNow });
                    return sut.ReceiveViaInbox("payload", CreateContext(messageId), () => Task.CompletedTask);
                },
                null);

            var thrown = (await act.Should().ThrowAsync<DbUpdateException>(
                "a flush failure the absorption gate does not cover must reach the caller")).Which;

            thrown.Entries.Select(entry => entry.Entity).OfType<InboxMessage>().Select(failing => failing.MessageId)
                .Should().NotContain(messageId, "the gate must have failed on the companion rather than matched on the claim");

            faultingCompanionWrite.InjectedFailureCount.Should().Be(1,
                "without an injected failure an empty change tracker proves nothing");

            context.ChangeTracker.Entries<InboxMessage>()
                .Should().BeEmpty("a claim the rolled back transaction never made durable must not stay tracked for the next lookup to find");

            var handlerInvoked = false;

            await unitOfWork.ExecuteAsync(
                _ => sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
                {
                    handlerInvoked = true;
                    return Task.CompletedTask;
                }),
                null);

            handlerInvoked.Should().BeTrue("a message id nothing ever committed must still reach the handler");
        }

        // INVARIANT: a claim whose flush failed with something TryClaimMessageIdAsync's catch (DbUpdateException)
        // cannot catch leaves nothing behind in the change tracker, so a redelivery over that same context reads the
        // store rather than the failed delivery's leftover Modified entry. Oracle for the uncaught-flush exit;
        // deleting the change-tracker clear on UnitOfWork's owned rollback path reddens this fact.
        //
        // The EXPIRED branch is the one that matters here: the leftover entry is Modified and carries the REFRESHED
        // timestamp as its current value, and that refreshed stamp is what HasMarkerExpired reads, so the
        // redelivery's lookup resolves a marker the deduplication window ages out in the store but not in the
        // identity map. A cancellation raised out of the flush leaves by this same exit - EF Core wraps a statement
        // failure into a DbUpdateException but passes an OperationCanceledException through untouched - so the exit
        // is a routine one rather than an exotic one.
        //
        // The CHANGE TRACKER and the SECOND DELIVERY REACHING ITS HANDLER are the discriminating observations. The
        // STORE is deliberately not asserted on: the stale marker sits there either way and nothing commits on this
        // path. InjectedFailureCount pins that the hook fired at all, without which an empty tracker reads
        // identically to a flush that simply succeeded.
        [Fact]
        public async Task MustNotSuppressARedeliveryOverTheSameContextWhenAnExpiredMarkersRefreshFailedOutsideDbUpdateException()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var messageId = Guid.NewGuid().ToString();
            var staleReceivedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            await GivenACommittedMarkerAsync(harness, messageId, staleReceivedAtUtc);

            var faultingSave = new FaultingInboxClaimSaveInterceptor();
            using var context = harness.CreateContext(options => options.AddInterceptors(faultingSave));
            var sut = CreateSqliteSut(context, TimeSpan.FromMinutes(1));
            var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

            Func<Task> act = () => unitOfWork.ExecuteAsync(
                _ => sut.ReceiveViaInbox("payload", CreateContext(messageId), () => Task.CompletedTask),
                null);

            await act.Should().ThrowAsync<InboxClaimFlushFaultException>(
                "a flush failure the claim's catch (DbUpdateException) cannot catch must reach the caller");

            faultingSave.InjectedFailureCount.Should().Be(1,
                "without an injected failure an empty change tracker proves nothing");

            context.ChangeTracker.Entries<InboxMessage>()
                .Should().BeEmpty("a refresh the rolled back transaction never made durable must not stay tracked for the next lookup to find");

            var handlerInvoked = false;

            await unitOfWork.ExecuteAsync(
                _ => sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
                {
                    handlerInvoked = true;
                    return Task.CompletedTask;
                }),
                null);

            handlerInvoked.Should().BeTrue("the redelivery must read the store's stale marker, which the window ages out");
        }

        // INVARIANT: a transaction carrying a claim whose handler did not return is refused at the commit point,
        // so a caller that swallows a claimed message's failure cannot commit the claim and suppress the message
        // for good. Oracle for the swallowed failure; deleting the unsettled-claim refusal in
        // PersistanceTransaction.CommitAsync reddens this fact.
        // The THROW is the discriminating assertion, not the empty store. On the defect the swallow leaves
        // ExecuteAsync returning normally - CompleteAsync's SaveChangesAsync is a no-op because the claim was
        // already accepted, and the commit succeeds - so an empty-store assertion alone would be blind: a variant
        // that never reached a commit leaves the store empty either way. The swallow is asserted to have happened
        // so the fact cannot pass vacuously on an operation that never threw at all.
        [Fact]
        public async Task MustRefuseTheCommitWhenAHandlerSwallowedAClaimedMessagesFailure()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var messageId = Guid.NewGuid().ToString();
            var swallowed = new InvalidOperationException("handler failed");
            var swallowedByTheCaller = false;

            using (var context = harness.CreateContext())
            {
                var sut = CreateSqliteSut(context);
                var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

                Func<Task> act = () => unitOfWork.ExecuteAsync(async _ =>
                {
                    try
                    {
                        await sut.ReceiveViaInbox<string>("payload", CreateContext(messageId), () => throw swallowed);
                    }
                    catch (Exception caught) when (ReferenceEquals(caught, swallowed))
                    {
                        swallowedByTheCaller = true;
                    }
                }, null);

                var refusal = (await act.Should().ThrowAsync<InvalidOperationException>(
                    "the commit point must refuse a transaction carrying a claim whose handler did not return")).Which;

                refusal.Should().NotBeSameAs(swallowed, "the refusal must be the commit point's, not the handler failure the caller swallowed");
                refusal.Message.Should().Contain(messageId, "the refusal must name the message id whose claim went unsettled");
            }

            swallowedByTheCaller.Should().BeTrue("the handler failure must actually have been swallowed, or this fact passes vacuously");

            using var verifyContext = harness.CreateContext();
            (await verifyContext.Set<InboxMessage>().ToListAsync())
                .Should().BeEmpty("a claim the refused commit never made durable must leave no marker behind");
        }

        // INVARIANT: settlement grants the commit. A handler that RETURNS settles its claim, so the unit of work
        // commits and the marker is durable. Oracle against a gate that refuses every commit, which would pass
        // MustRefuseTheCommitWhenAHandlerSwallowedAClaimedMessagesFailure just as well.
        [Fact]
        public async Task MustCommitTheClaimWhenTheHandlerReturns()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var messageId = Guid.NewGuid().ToString();
            var handlerInvoked = false;

            using (var context = harness.CreateContext())
            {
                var sut = CreateSqliteSut(context);
                var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

                await unitOfWork.ExecuteAsync(
                    _ => sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
                    {
                        handlerInvoked = true;
                        return Task.CompletedTask;
                    }),
                    null);
            }

            handlerInvoked.Should().BeTrue();

            using var verifyContext = harness.CreateContext();
            (await verifyContext.Set<InboxMessage>().Select(m => m.MessageId).ToListAsync())
                .Should().ContainSingle().Which.Should().Be(messageId, "a handler that returned must leave its claim durable");
        }

        // INVARIANT: ownership is a fact about the TRANSACTION OBJECT, not about which unit of work is innermost.
        // A unit of work nested inside another's transaction begins none of its own and owns nothing, yet the
        // transaction it participates in is still one a unit of work began, so a dispatch nested inside another
        // handler claims exactly as a top-level one does. Oracle for the keying; recording ownership against the
        // SCOPE rather than the transaction object - so that only the unit of work whose own ExecuteAsync is
        // running counts as owner - reddens this fact and no other, measured by making that substitution and
        // counting, and would otherwise silently refuse every nested dispatch and break the
        // multiple-claims-per-transaction shape InboxClaimRegister exists to support. It does NOT
        // pin the refusal - keying that called every transaction owned passes here - which
        // MustRefuseToClaimInsideATransactionNoUnitOfWorkBegan pins instead.
        [Fact]
        public async Task MustClaimInsideAUnitOfWorkNestedInAnothersTransaction()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var messageId = Guid.NewGuid().ToString();
            var handlerInvoked = false;

            using (var context = harness.CreateContext())
            {
                var sut = CreateSqliteSut(context);
                var outerUnitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);
                var nestedUnitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

                await outerUnitOfWork.ExecuteAsync(
                    _ => nestedUnitOfWork.ExecuteAsync(
                        __ => sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
                        {
                            handlerInvoked = true;
                            return Task.CompletedTask;
                        }),
                        null),
                    null);
            }

            handlerInvoked.Should().BeTrue("a dispatch nested inside another unit of work must still reach the handler");

            using var verifyContext = harness.CreateContext();
            (await verifyContext.Set<InboxMessage>().Select(m => m.MessageId).ToListAsync())
                .Should().ContainSingle().Which.Should().Be(messageId,
                    "the nested claim must be made durable by the outer unit of work's commit");
        }

        // INVARIANT: the refusal is decided PER CLAIM, so a later claim that settled cannot grant the commit an
        // earlier unsettled one withholds. Oracle for the per-claim reading; a single per-transaction flag that the
        // last settlement clears passes MustRefuseTheCommitWhenAHandlerSwallowedAClaimedMessagesFailure and reddens
        // here. The swallowed delivery is ordered FIRST for exactly that reason - a last-writer flag is only
        // distinguishable when a settlement follows the claim that went unsettled.
        [Fact]
        public async Task MustRefuseTheCommitWhenOnlyOneOfTwoClaimsWasSwallowed()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var swallowedMessageId = Guid.NewGuid().ToString();
            var handledMessageId = Guid.NewGuid().ToString();
            var swallowed = new InvalidOperationException("handler failed");
            var swallowedByTheCaller = false;
            var secondHandlerInvoked = false;

            using (var context = harness.CreateContext())
            {
                var sut = CreateSqliteSut(context);
                var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

                Func<Task> act = () => unitOfWork.ExecuteAsync(async _ =>
                {
                    try
                    {
                        await sut.ReceiveViaInbox<string>("payload", CreateContext(swallowedMessageId), () => throw swallowed);
                    }
                    catch (Exception caught) when (ReferenceEquals(caught, swallowed))
                    {
                        swallowedByTheCaller = true;
                    }

                    await sut.ReceiveViaInbox("payload", CreateContext(handledMessageId), () =>
                    {
                        secondHandlerInvoked = true;
                        return Task.CompletedTask;
                    });
                }, null);

                (await act.Should().ThrowAsync<InvalidOperationException>())
                    .Which.Message.Should().Contain(swallowedMessageId, "the refusal must name the claim that went unsettled");
            }

            swallowedByTheCaller.Should().BeTrue("the first handler's failure must actually have been swallowed");
            secondHandlerInvoked.Should().BeTrue("the second delivery must have claimed and settled, or there is no settlement to out-weigh");

            using var verifyContext = harness.CreateContext();
            (await verifyContext.Set<InboxMessage>().ToListAsync())
                .Should().BeEmpty("neither claim may be made durable by a commit the refusal withheld");
        }

        [Fact]
        public void MustThrowWhenContextIsNull()
        {
            Action act = () => new BrokeredMessageInbox<DbContext>(null, _logger, _options);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenLoggerIsNull()
        {
            Action act = () => new BrokeredMessageInbox<DbContext>(_context, null, _options);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenOptionsIsNull()
        {
            Action act = () => new BrokeredMessageInbox<DbContext>(_context, _logger, null);

            act.Should().Throw<ArgumentNullException>();
        }

        // INVARIANT: with no Deduplication Window configured - the default - an existing marker suppresses the
        // redelivery however old it is. This is the 0.8.0 behaviour and expiry must not change it.
        [Fact]
        public async Task MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, DateTime.UtcNow.AddYears(-5));

            var context = CreateContext(messageId);
            var handlerInvoked = false;

            await _sut.ReceiveViaInbox("payload", context, () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            handlerInvoked.Should().BeFalse();
            var persisted = await _dbContext.Set<InboxMessage>().ToListAsync();
            persisted.Should().ContainSingle();
        }

        [Fact]
        public async Task MustSkipHandlerForAnyExistingMarkerWhenRetentionIsExplicitlyDisabled()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, DateTime.UtcNow.AddYears(-5));

            var sut = CreateSutWithDeduplicationWindow(null);
            var handlerInvoked = false;

            await sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            handlerInvoked.Should().BeFalse();
        }

        // INVARIANT: MessageId is the inbox primary key, so an expired marker is REFRESHED in place rather than
        // inserted a second time, and that refresh reaches the store before the handler runs exactly as a fresh
        // claim does. Oracle for the expired branch of the claim; moving the flush after the handler reddens it.
        [Fact]
        public async Task MustRefreshTheClaimBeforeInvokingTheHandlerForAnExpiredMessageId()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            var messageId = Guid.NewGuid().ToString();
            var staleReceivedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            await GivenACommittedMarkerAsync(harness, messageId, staleReceivedAtUtc);

            using var context = harness.CreateContext();
            var sut = CreateSqliteSut(context, TimeSpan.FromMinutes(1));
            var unitOfWork = new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

            DateTime? refreshVisibleToTheHandler = null;
            var handlerInvoked = false;

            await unitOfWork.ExecuteAsync(
                _ => sut.ReceiveViaInbox("payload", CreateContext(messageId), async () =>
                {
                    using var reader = CreateReaderEnlistedIn(harness, context);
                    refreshVisibleToTheHandler = await reader.Set<InboxMessage>()
                        .Where(m => m.MessageId == messageId)
                        .Select(m => m.ReceivedByInboxAtUtc)
                        .SingleAsync();
                    handlerInvoked = true;
                }),
                null);

            handlerInvoked.Should().BeTrue();
            refreshVisibleToTheHandler.Should().BeAfter(staleReceivedAtUtc,
                "the refreshed claim must reach the store before the handler is invoked");

            using var verifyContext = harness.CreateContext();
            (await verifyContext.Set<InboxMessage>().ToListAsync())
                .Should().ContainSingle("the expired marker is refreshed in place, not inserted a second time");
        }

        [Fact]
        public async Task MustSkipHandlerWhenMarkerIsWithinTheDeduplicationWindow()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, DateTime.UtcNow.AddMinutes(-1));

            var sut = CreateSutWithDeduplicationWindow(TimeSpan.FromHours(1));
            var handlerInvoked = false;

            await sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            handlerInvoked.Should().BeFalse();
            _dbContext.ChangeTracker.Entries<InboxMessage>().Single().State.Should().Be(EntityState.Unchanged);
        }

        // INVARIANT: a marker with no timestamp cannot be aged, so it keeps suppressing. Expiring it would let a
        // window silently undo a suppression the inbox cannot date.
        [Fact]
        public async Task MustSkipHandlerWhenMarkerHasNoTimestampAndDeduplicationWindowIsSet()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, null);

            var sut = CreateSutWithDeduplicationWindow(TimeSpan.FromMinutes(1));
            var handlerInvoked = false;

            await sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            handlerInvoked.Should().BeFalse();
        }

        // INVARIANT: the token threads through the inbox lookup, the claim's flush and the duplicate re-read, so a
        // token already cancelled stops the delivery before the handler. Oracle for cancellation on the claim path.
        [Fact]
        public async Task MustThrowBeforeInvokingHandlerWhenCancellationIsAlreadyRequested()
        {
            using var harness = InboxClaimSqliteHarness.Create();
            using var context = harness.CreateContext();
            var sut = CreateSqliteSut(context);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            using var transaction = await context.Database.BeginTransactionAsync();

            var handlerInvoked = false;

            Func<Task> act = () => sut.ReceiveViaInbox("payload", CreateContext(Guid.NewGuid().ToString(), cancellation.Token), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            await act.Should().ThrowAsync<OperationCanceledException>();
            handlerInvoked.Should().BeFalse();
        }

        // INVARIANT: suppression is logged at Information with the message id. At Trace a host that dropped a
        // message it should have handled had nothing in its logs saying so.
        [Fact]
        public async Task MustLogSuppressionAtInformationWithTheMessageId()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, DateTime.UtcNow);

            await _sut.ReceiveViaInbox("payload", CreateContext(messageId), () => Task.CompletedTask);

            _loggerCreator.LoggedMessages
                .Should()
                .Contain(logged => logged.level == LogLevel.Information && logged.message.Contains(messageId));
        }

        [Fact]
        public async Task MustNotReportReceivedForAMarkerOlderThanTheDeduplicationWindow()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, DateTime.UtcNow.AddMinutes(-10));

            var sut = CreateSutWithDeduplicationWindow(TimeSpan.FromMinutes(1));

            (await sut.HasBeenReceived(messageId)).Should().BeFalse();
        }

        [Fact]
        public async Task MustReportReceivedForAMarkerWithinTheDeduplicationWindow()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, DateTime.UtcNow.AddMinutes(-1));

            var sut = CreateSutWithDeduplicationWindow(TimeSpan.FromHours(1));

            (await sut.HasBeenReceived(messageId)).Should().BeTrue();
        }

        [Fact]
        public async Task MustReportReceivedForAnyExistingMarkerWhenDeduplicationWindowIsUnset()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, DateTime.UtcNow.AddYears(-5));

            (await _sut.HasBeenReceived(messageId)).Should().BeTrue();
        }

        [Fact]
        public void MustThrowWhenRetentionOptionsIsNull()
        {
            Action act = () => new BrokeredMessageInbox<DbContext>(_context, _logger, _options, null);

            act.Should().Throw<ArgumentNullException>();
        }
    }
}
