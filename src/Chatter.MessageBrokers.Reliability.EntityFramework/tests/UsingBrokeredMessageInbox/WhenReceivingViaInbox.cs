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
    // The facts that reach a CLAIM run over the relational harness rather than the InMemory provider: the claim is
    // flushed into the context's ambient transaction, and the InMemory provider supplies none, so every one of them
    // would meet the no-transaction refusal instead of the behaviour under test. The facts that only SUPPRESS, only
    // read through HasBeenReceived, or only exercise the constructor never reach a claim and stay on the InMemory
    // context they were written against.
    public class WhenReceivingViaInbox : Testing.Core.Context, IAsyncDisposable
    {
        private DbContextCreator _context;
        private readonly DbContext _dbContext;
        private readonly BrokeredMessageInbox<DbContext> _sut;
        private readonly LoggerCreator<BrokeredMessageInbox<DbContext>> _loggerCreator;
        private readonly ILogger<BrokeredMessageInbox<DbContext>> _logger;
        private readonly ReliabilityOptions _options;
        private readonly InboxClaimSqliteHarness _harness;
        private readonly InboxClaimSqliteContext _relationalContext;
        private readonly BrokeredMessageInbox<InboxClaimSqliteContext> _relationalSut;
        private readonly IUnitOfWork _relationalUnitOfWork;

        public WhenReceivingViaInbox()
        {
            _context = New.MessageBrokers().DbContext();
            _dbContext = _context;
            _loggerCreator = New.Common().Logger<BrokeredMessageInbox<DbContext>>();
            _logger = _loggerCreator.Creation;
            _options = new ReliabilityOptions();
            _sut = new BrokeredMessageInbox<DbContext>(_context, _logger, _options);
            _harness = InboxClaimSqliteHarness.Create();
            _relationalContext = _harness.CreateContext();
            _relationalSut = CreateInbox(_relationalContext);
            _relationalUnitOfWork = CreateUnitOfWork(_relationalContext);
        }

        public async ValueTask DisposeAsync()
        {
            await _relationalContext.DisposeAsync();
            await _harness.DisposeAsync();
        }

        private BrokeredMessageInbox<DbContext> CreateSutWithDeduplicationWindow(TimeSpan? deduplicationWindow)
            => new BrokeredMessageInbox<DbContext>(_context,
                                                   _logger,
                                                   _options,
                                                   new EntityFrameworkReliabilityOptions { InboxDeduplicationWindow = deduplicationWindow });

        private static BrokeredMessageInbox<InboxClaimSqliteContext> CreateInbox(InboxClaimSqliteContext context,
                                                                                 TimeSpan? deduplicationWindow = null)
            => new BrokeredMessageInbox<InboxClaimSqliteContext>(
                context,
                NullLogger<BrokeredMessageInbox<InboxClaimSqliteContext>>.Instance,
                new ReliabilityOptions(),
                new EntityFrameworkReliabilityOptions { InboxDeduplicationWindow = deduplicationWindow });

        // NullLogger rather than a Moq double: UnitOfWork<TContext> is internal, and Castle DynamicProxy refuses to
        // proxy ILogger<T> when T names a type it cannot see from its own strong-named proxy assembly.
        private static IUnitOfWork CreateUnitOfWork(InboxClaimSqliteContext context)
            => new UnitOfWork<InboxClaimSqliteContext>(context, NullLogger<UnitOfWork<InboxClaimSqliteContext>>.Instance);

        private InboxMessage GivenAMarker(string messageId, DateTime? receivedAtUtc)
        {
            var marker = new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = receivedAtUtc };
            _dbContext.Add(marker);
            _dbContext.SaveChanges();

            return marker;
        }

        // Seeded through its own context so the context under test starts with an empty change tracker and has to
        // read the row from the store, the way a redelivery on a fresh scope does.
        private void GivenACommittedMarker(string messageId, DateTime? receivedAtUtc)
        {
            using var seedContext = _harness.CreateContext();
            seedContext.Add(new InboxMessage { MessageId = messageId, ReceivedByInboxAtUtc = receivedAtUtc });
            seedContext.SaveChanges();
        }

        // AsNoTracking so the answer comes from the store rather than the identity map: a claim that was staged and
        // never flushed is absent here, which is the difference these facts turn on.
        private static Task<InboxMessage> ReadMarkerAsync(InboxClaimSqliteContext context, string messageId)
            => context.Set<InboxMessage>().AsNoTracking().SingleOrDefaultAsync(m => m.MessageId == messageId);

        private async Task<InboxMessage> ReadCommittedMarkerAsync(string messageId)
        {
            using var freshContext = _harness.CreateContext();
            return await ReadMarkerAsync(freshContext, messageId);
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

        // INVARIANT: the row is read back FROM THE STORE at handler entry, and both halves of the assertion carry
        // weight: its presence reddens on a claim that was staged and never flushed, and its null timestamp
        // reddens on a claim stamped handled before the handler ran.
        [Fact]
        public async Task MustFlushAClaimCarryingNoTimestampBeforeInvokingTheHandler()
        {
            var messageId = Guid.NewGuid().ToString();
            var handlerInvoked = false;
            InboxMessage claimAtHandlerEntry = null;

            await _relationalUnitOfWork.ExecuteAsync(
                ct => _relationalSut.ReceiveViaInbox("payload", CreateContext(messageId, ct), async () =>
                {
                    handlerInvoked = true;
                    claimAtHandlerEntry = await ReadMarkerAsync(_relationalContext, messageId);
                }),
                null);

            handlerInvoked.Should().BeTrue();
            claimAtHandlerEntry.Should().NotBeNull();
            claimAtHandlerEntry.ReceivedByInboxAtUtc.Should().BeNull();
        }

        // The stamp is read back FROM THE STORE while the transaction is still open, before any commit, because a
        // stamp merely staged on the tracked instance is indistinguishable from a flushed one once the unit of
        // work's own SaveChangesAsync has run. This read is what separates them.
        [Fact]
        public async Task MustFlushTheStampWhenTheHandlerReturnsAndCommitIt()
        {
            var messageId = Guid.NewGuid().ToString();
            var handlerInvoked = false;
            InboxMessage markerWhenReceiveReturned = null;

            await _relationalUnitOfWork.ExecuteAsync(async ct =>
            {
                await _relationalSut.ReceiveViaInbox("payload", CreateContext(messageId, ct), () =>
                {
                    handlerInvoked = true;
                    return Task.CompletedTask;
                });

                markerWhenReceiveReturned = await ReadMarkerAsync(_relationalContext, messageId);
            }, null);

            handlerInvoked.Should().BeTrue();
            markerWhenReceiveReturned.Should().NotBeNull();
            markerWhenReceiveReturned.ReceivedByInboxAtUtc.Should().NotBeNull();

            var committedMarker = await ReadCommittedMarkerAsync(messageId);
            committedMarker.Should().NotBeNull();
            committedMarker.ReceivedByInboxAtUtc.Should().NotBeNull();
        }

        // The handler clears the change tracker of the SAME context the claim was flushed through, which is what a
        // handler that batches its own work over the shared TContext ordinarily does. The claim is then detached,
        // so a stamp written onto it alone reaches no row and the transaction commits the claim still carrying no
        // timestamp - which the next delivery reads as unhandled.
        [Fact]
        public async Task MustStampAClaimTheHandlerDetachedFromTheChangeTracker()
        {
            var messageId = Guid.NewGuid().ToString();
            var handlerInvoked = false;

            await _relationalUnitOfWork.ExecuteAsync(
                ct => _relationalSut.ReceiveViaInbox("payload", CreateContext(messageId, ct), () =>
                {
                    handlerInvoked = true;
                    _relationalContext.ChangeTracker.Clear();
                    return Task.CompletedTask;
                }),
                null);

            handlerInvoked.Should().BeTrue();

            var committedMarker = await ReadCommittedMarkerAsync(messageId);
            committedMarker.Should().NotBeNull();
            committedMarker.ReceivedByInboxAtUtc.Should().NotBeNull();
        }

        [Fact]
        public async Task MustFlushTheClaimWithoutCommittingIt()
        {
            var messageId = Guid.NewGuid().ToString();
            var commitCounter = new InboxCommitCountingTransactionInterceptor();
            using var countingContext = _harness.CreateContext(options => options.AddInterceptors(commitCounter));
            var sut = CreateInbox(countingContext);
            var unitOfWork = CreateUnitOfWork(countingContext);
            var commitCountAtHandlerEntry = -1;
            var commitCountWhenReceiveReturned = -1;

            await unitOfWork.ExecuteAsync(async ct =>
            {
                await sut.ReceiveViaInbox("payload", CreateContext(messageId, ct), () =>
                {
                    commitCountAtHandlerEntry = commitCounter.CommitCount;
                    return Task.CompletedTask;
                });

                commitCountWhenReceiveReturned = commitCounter.CommitCount;
            }, null);

            commitCountAtHandlerEntry.Should().Be(0);
            commitCountWhenReceiveReturned.Should().Be(0);
            commitCounter.CommitCount.Should().Be(1);
            (await ReadCommittedMarkerAsync(messageId)).Should().NotBeNull();
        }

        [Fact]
        public async Task MustRefuseToClaimOutsideATransaction()
        {
            var messageId = Guid.NewGuid().ToString();
            var handlerInvoked = false;

            Func<Task> act = () => _relationalSut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain(nameof(InboxClaimSqliteContext));
            handlerInvoked.Should().BeFalse();
            (await ReadCommittedMarkerAsync(messageId)).Should().BeNull();
        }

        // INVARIANT: a caller that CATCHES the handler's failure and returns normally commits a claim carrying no
        // timestamp, and the next delivery reads that as unhandled and runs the handler. The swallow is harmless
        // rather than forbidden: the caller's own work lands and the message is not lost. Writing the handled time
        // at claim time reddens this fact, and so does reading a row with no timestamp as handled - both measured
        // on net8.0.
        [Fact]
        public async Task MustCommitAnUnstampedClaimWhenACallerSwallowsTheHandlersFailure()
        {
            var messageId = Guid.NewGuid().ToString();
            var handlerFailure = new InvalidOperationException("handler failed");
            Exception swallowedFailure = null;

            Func<Task> commit = () => _relationalUnitOfWork.ExecuteAsync(async ct =>
            {
                try
                {
                    await _relationalSut.ReceiveViaInbox<string>("payload", CreateContext(messageId, ct), () => throw handlerFailure);
                }
                catch (InvalidOperationException ex)
                {
                    swallowedFailure = ex;
                }
            }, null);

            await commit.Should().NotThrowAsync();

            swallowedFailure.Should().BeSameAs(handlerFailure);
            var committedMarker = await ReadCommittedMarkerAsync(messageId);
            committedMarker.Should().NotBeNull();
            committedMarker.ReceivedByInboxAtUtc.Should().BeNull();

            using var redeliveryContext = _harness.CreateContext();
            var redeliverySut = CreateInbox(redeliveryContext);
            var redeliveryHandlerInvoked = false;

            await CreateUnitOfWork(redeliveryContext).ExecuteAsync(
                ct => redeliverySut.ReceiveViaInbox("payload", CreateContext(messageId, ct), () =>
                {
                    redeliveryHandlerInvoked = true;
                    return Task.CompletedTask;
                }),
                null);

            redeliveryHandlerInvoked.Should().BeTrue();
            (await ReadCommittedMarkerAsync(messageId)).ReceivedByInboxAtUtc.Should().NotBeNull();
        }

        // INVARIANT: each message id's answer lives in its own row, so one delivery nested inside another settles
        // independently of it even though both rows commit together. Writing the handled time at claim time
        // reddens this fact, measured on net8.0.
        [Fact]
        public async Task MustCarryEachClaimsOwnOutcomeWhenANestedDeliverysFailureIsSwallowed()
        {
            var outerMessageId = Guid.NewGuid().ToString();
            var nestedMessageId = Guid.NewGuid().ToString();
            var nestedFailure = new InvalidOperationException("nested handler failed");
            Exception swallowedFailure = null;

            await _relationalUnitOfWork.ExecuteAsync(
                ct => _relationalSut.ReceiveViaInbox("outer", CreateContext(outerMessageId, ct), async () =>
                {
                    await _relationalUnitOfWork.ExecuteAsync(async nestedCt =>
                    {
                        try
                        {
                            await _relationalSut.ReceiveViaInbox<string>("nested", CreateContext(nestedMessageId, nestedCt), () => throw nestedFailure);
                        }
                        catch (InvalidOperationException ex)
                        {
                            swallowedFailure = ex;
                        }
                    }, null);
                }),
                null);

            swallowedFailure.Should().BeSameAs(nestedFailure);
            (await ReadCommittedMarkerAsync(outerMessageId)).ReceivedByInboxAtUtc.Should().NotBeNull();
            (await ReadCommittedMarkerAsync(nestedMessageId)).ReceivedByInboxAtUtc.Should().BeNull();
        }

        // INVARIANT: the claim was flushed and stamped, so the change tracker holds a stamped row the store never
        // kept once the commit failed. The redelivery over that same scoped context must still run the handler,
        // which it does because UnitOfWork reconciles the tracker on its rollback. Removing that clear from
        // UnitOfWork.ExecuteAsync's catch reddens this fact and one other, measured on net8.0.
        [Fact]
        public async Task MustNotSuppressARedeliveryOverTheSameContextWhenTheCommitFailedAfterTheHandlerReturned()
        {
            var messageId = Guid.NewGuid().ToString();
            using var faultingContext = _harness.CreateContext(
                options => options.ReplaceService<IRelationalTransactionFactory, CommitFaultingRelationalTransactionFactory>());
            var sut = CreateInbox(faultingContext);
            var unitOfWork = CreateUnitOfWork(faultingContext);
            var firstHandlerInvoked = false;
            var redeliveryHandlerInvoked = false;

            Func<Task> firstDelivery = () => unitOfWork.ExecuteAsync(
                ct => sut.ReceiveViaInbox("payload", CreateContext(messageId, ct), () =>
                {
                    firstHandlerInvoked = true;
                    return Task.CompletedTask;
                }),
                null);

            await firstDelivery.Should().ThrowAsync<TransactionFaultException>();
            firstHandlerInvoked.Should().BeTrue();

            Func<Task> redelivery = () => unitOfWork.ExecuteAsync(
                ct => sut.ReceiveViaInbox("payload", CreateContext(messageId, ct), () =>
                {
                    redeliveryHandlerInvoked = true;
                    return Task.CompletedTask;
                }),
                null);

            await redelivery.Should().ThrowAsync<TransactionFaultException>();
            redeliveryHandlerInvoked.Should().BeTrue();
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

        [Fact]
        public async Task MustPropagateHandlerExceptionAndNotPersistInboxMessage()
        {
            var messageId = Guid.NewGuid().ToString();
            var expected = new InvalidOperationException("handler failed");

            Func<Task> act = () => _relationalUnitOfWork.ExecuteAsync(
                ct => _relationalSut.ReceiveViaInbox<string>("payload", CreateContext(messageId, ct), () => throw expected),
                null);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(expected);
            (await ReadCommittedMarkerAsync(messageId)).Should().BeNull();
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
        // inserted a second time, and the refresh takes the row through the SAME two states a fresh message id
        // does - claimed with no timestamp, then stamped - rather than carrying its stale timestamp across the
        // handler call.
        [Fact]
        public async Task MustInvokeHandlerAndRefreshTheMarkerWhenDeduplicationWindowHasElapsed()
        {
            var messageId = Guid.NewGuid().ToString();
            var staleReceivedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            GivenACommittedMarker(messageId, staleReceivedAtUtc);

            var sut = CreateInbox(_relationalContext, TimeSpan.FromMinutes(1));
            var handlerInvoked = false;
            InboxMessage claimAtHandlerEntry = null;

            await _relationalUnitOfWork.ExecuteAsync(
                ct => sut.ReceiveViaInbox("payload", CreateContext(messageId, ct), async () =>
                {
                    handlerInvoked = true;
                    claimAtHandlerEntry = await ReadMarkerAsync(_relationalContext, messageId);
                }),
                null);

            handlerInvoked.Should().BeTrue();
            claimAtHandlerEntry.Should().NotBeNull();
            claimAtHandlerEntry.ReceivedByInboxAtUtc.Should().BeNull();

            using var freshContext = _harness.CreateContext();
            var persisted = await freshContext.Set<InboxMessage>().AsNoTracking().ToListAsync();
            persisted.Should().ContainSingle().Which.ReceivedByInboxAtUtc.Should().BeAfter(staleReceivedAtUtc);
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

        // INVARIANT: a row carrying no timestamp records a CLAIM whose handler never completed, so the handler runs
        // again whatever the Deduplication Window says - a row with no timestamp has no age to compare it against.
        // These two facts fix the window's two settings between them: a predicate that read no timestamp as a
        // timestamp of DateTime.MinValue passes the first and fails the second, and one that read it as handled
        // fails both. Measured on net8.0, not predicted.
        [Fact]
        public async Task MustInvokeHandlerForAClaimWithNoTimestampWhenDeduplicationWindowIsSet()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenACommittedMarker(messageId, null);

            var sut = CreateInbox(_relationalContext, TimeSpan.FromMinutes(1));
            var handlerInvoked = false;

            await _relationalUnitOfWork.ExecuteAsync(
                ct => sut.ReceiveViaInbox("payload", CreateContext(messageId, ct), () =>
                {
                    handlerInvoked = true;
                    return Task.CompletedTask;
                }),
                null);

            handlerInvoked.Should().BeTrue();
            (await ReadCommittedMarkerAsync(messageId)).ReceivedByInboxAtUtc.Should().NotBeNull();
        }

        [Fact]
        public async Task MustInvokeHandlerForAClaimWithNoTimestampWhenDeduplicationWindowIsUnset()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenACommittedMarker(messageId, null);

            var handlerInvoked = false;

            await _relationalUnitOfWork.ExecuteAsync(
                ct => _relationalSut.ReceiveViaInbox("payload", CreateContext(messageId, ct), () =>
                {
                    handlerInvoked = true;
                    return Task.CompletedTask;
                }),
                null);

            handlerInvoked.Should().BeTrue();
            (await ReadCommittedMarkerAsync(messageId)).ReceivedByInboxAtUtc.Should().NotBeNull();
        }

        [Fact]
        public async Task MustThrowBeforeInvokingHandlerWhenCancellationIsAlreadyRequested()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var context = CreateContext(Guid.NewGuid().ToString(), cancellation.Token);
            var handlerInvoked = false;

            Func<Task> act = () => _sut.ReceiveViaInbox("payload", context, () =>
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

        // INVARIANT: HasBeenReceived answers for the HANDLER, not for the row, so a claim carrying no timestamp
        // reports not-received under either setting of the Deduplication Window. A deduplicator that answered from
        // the row's presence alone would tell a caller a message was handled while its handler was still running -
        // or had already failed.
        [Fact]
        public async Task MustNotReportReceivedForAClaimWithNoTimestampWhenDeduplicationWindowIsUnset()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, null);

            (await _sut.HasBeenReceived(messageId)).Should().BeFalse();
        }

        [Fact]
        public async Task MustNotReportReceivedForAClaimWithNoTimestampWhenDeduplicationWindowIsSet()
        {
            var messageId = Guid.NewGuid().ToString();
            GivenAMarker(messageId, null);

            var sut = CreateSutWithDeduplicationWindow(TimeSpan.FromHours(1));

            (await sut.HasBeenReceived(messageId)).Should().BeFalse();
        }

        [Fact]
        public void MustThrowWhenRetentionOptionsIsNull()
        {
            Action act = () => new BrokeredMessageInbox<DbContext>(_context, _logger, _options, null);

            act.Should().Throw<ArgumentNullException>();
        }
    }
}
