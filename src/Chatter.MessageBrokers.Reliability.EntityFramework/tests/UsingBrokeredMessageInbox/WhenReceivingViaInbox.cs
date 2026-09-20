using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        // INVARIANT: ReceiveViaInbox adds the inbox message via DbSet.AddAsync but never calls
        // SaveChangesAsync. AS-IS the new inbox row is only tracked as Added; it is not persisted
        // to the store, so a fresh query returns nothing until the surrounding context is saved.
        [Fact]
        public async Task MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId()
        {
            var messageId = Guid.NewGuid().ToString();
            var context = CreateContext(messageId);
            var handlerInvoked = false;

            await _sut.ReceiveViaInbox("payload", context, () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            handlerInvoked.Should().BeTrue();

            var tracked = _dbContext.ChangeTracker.Entries<InboxMessage>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .Single();
            tracked.MessageId.Should().Be(messageId);
            tracked.ReceivedByInboxAtUtc.Should().NotBeNull();

            var persisted = await _dbContext.Set<InboxMessage>().ToListAsync();
            persisted.Should().BeEmpty();
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
            var context = CreateContext(messageId);
            var expected = new InvalidOperationException("handler failed");

            Func<Task> act = () => _sut.ReceiveViaInbox<string>("payload", context, () => throw expected);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(expected);
            var persisted = await _dbContext.Set<InboxMessage>().ToListAsync();
            persisted.Should().BeEmpty();
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
        // inserted a second time. The refresh is a tracked Modified entry so it commits in the same transaction as
        // the handler's own work.
        [Fact]
        public async Task MustInvokeHandlerAndRefreshTheMarkerWhenDeduplicationWindowHasElapsed()
        {
            var messageId = Guid.NewGuid().ToString();
            var staleReceivedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            GivenAMarker(messageId, staleReceivedAtUtc);

            var sut = CreateSutWithDeduplicationWindow(TimeSpan.FromMinutes(1));
            var handlerInvoked = false;

            await sut.ReceiveViaInbox("payload", CreateContext(messageId), () =>
            {
                handlerInvoked = true;
                return Task.CompletedTask;
            });

            handlerInvoked.Should().BeTrue();

            var entry = _dbContext.ChangeTracker.Entries<InboxMessage>().Single();
            entry.State.Should().Be(EntityState.Modified);
            entry.Entity.ReceivedByInboxAtUtc.Should().BeAfter(staleReceivedAtUtc);

            var persisted = await _dbContext.Set<InboxMessage>().ToListAsync();
            persisted.Should().ContainSingle();
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

        [Fact]
        public void MustThrowWhenRetentionOptionsIsNull()
        {
            Action act = () => new BrokeredMessageInbox<DbContext>(_context, _logger, _options, null);

            act.Should().Throw<ArgumentNullException>();
        }

        // INVARIANT: BrokeredMessageInbox declares no DbContext-assignable field. This guard buys a
        // narrowed declared surface — no _context.SaveChangesAsync(...) in the type's own vocabulary
        // — and a regression tripwire against reintroducing a DbContext field, the exact path the
        // reverted inbox self-save took. It does not make a commit impossible: DbSet<T> transitively
        // reaches the DbContext (EF Core 10.0.0: DbSet<T> declares IInfrastructure<IServiceProvider>;
        // the runtime InternalDbSet<T> implements IInfrastructure<DbContext> and holds a private
        // DbContext field), so a commit is reachable from the retained DbSet<InboxMessage> with no
        // reflection and no internal-type cast. What actually enforces that the marker is committed
        // exactly once by UnitOfWorkBehavior's single SaveChangesAsync is the canonical resolved order
        // [OutboxProcessingBehavior, UnitOfWorkBehavior, InboxBehavior] plus the characterization test
        // MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId. See
        // docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md.
        [Fact]
        public void MustNotDeclareADbContextField()
        {
            var dbContextFields = typeof(BrokeredMessageInbox<DbContext>)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(field => typeof(DbContext).IsAssignableFrom(field.FieldType))
                .Select(field => $"{field.FieldType.Name} {field.Name}");

            dbContextFields.Should().BeEmpty();
        }
    }
}
