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
        private readonly ILogger<BrokeredMessageInbox<DbContext>> _logger;
        private readonly ReliabilityOptions _options;

        public WhenReceivingViaInbox()
        {
            _context = New.MessageBrokers().DbContext();
            _dbContext = _context;
            _logger = New.Common().Logger<BrokeredMessageInbox<DbContext>>().Creation;
            _options = new ReliabilityOptions();
            _sut = new BrokeredMessageInbox<DbContext>(_context, _logger, _options);
        }

        private static IMessageBrokerContext CreateContext(string messageId)
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.Setup(c => c.ContentType).Returns("application/json");

            return new MessageBrokerContext(
                messageId,
                Array.Empty<byte>(),
                new Dictionary<string, object>(),
                "test-receiver",
                CancellationToken.None,
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
    }
}
