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
            var messageId = Guid.NewGuid().ToString();

            using var transaction = await context.Database.BeginTransactionAsync();

            var claimVisibleToTheHandler = false;
            var handlerInvoked = false;

            await sut.ReceiveViaInbox("payload", CreateContext(messageId), async () =>
            {
                using var reader = CreateReaderEnlistedIn(harness, context);
                claimVisibleToTheHandler = await reader.Set<InboxMessage>().AnyAsync(m => m.MessageId == messageId);
                handlerInvoked = true;
            });

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
            var messageId = Guid.NewGuid().ToString();

            using var transaction = await context.Database.BeginTransactionAsync();

            await sut.ReceiveViaInbox("payload", CreateContext(messageId), () => Task.CompletedTask);

            using (var reader = CreateReaderEnlistedIn(harness, context))
            {
                (await reader.Set<InboxMessage>().AnyAsync(m => m.MessageId == messageId))
                    .Should().BeTrue("the claim must have been flushed into the ambient transaction");
            }

            commitCounter.CommitCount.Should().Be(0, "the inbox must leave the commit to the unit of work");

            await transaction.CommitAsync();

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

            using var transaction = await context.Database.BeginTransactionAsync();

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

            await transaction.CommitAsync();

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

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("WithInboxBehavior", "the refusal must name the registration that fixes it");

            handlerInvoked.Should().BeFalse("the refusal must come before the handler");
            context.ChangeTracker.Entries<InboxMessage>().Should().BeEmpty("the refusal must come before anything is staged");

            using var verifyContext = harness.CreateContext();
            (await verifyContext.Set<InboxMessage>().ToListAsync()).Should().BeEmpty();
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
        // branch; deleting the catch around the handler in ReceiveViaInbox reddens this fact. See
        // docs/adr/0033-the-relational-inbox-claims-the-message-id-before-the-handler-inside-the-ambient-transaction.md.
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
        // branch; deleting the catch around the handler in ReceiveViaInbox reddens this fact.
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

            using var transaction = await context.Database.BeginTransactionAsync();

            DateTime? refreshVisibleToTheHandler = null;
            var handlerInvoked = false;

            await sut.ReceiveViaInbox("payload", CreateContext(messageId), async () =>
            {
                using var reader = CreateReaderEnlistedIn(harness, context);
                refreshVisibleToTheHandler = await reader.Set<InboxMessage>()
                    .Where(m => m.MessageId == messageId)
                    .Select(m => m.ReceivedByInboxAtUtc)
                    .SingleAsync();
                handlerInvoked = true;
            });

            handlerInvoked.Should().BeTrue();
            refreshVisibleToTheHandler.Should().BeAfter(staleReceivedAtUtc,
                "the refreshed claim must reach the store before the handler is invoked");

            await transaction.CommitAsync();

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
