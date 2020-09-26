using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    public class WhenGettingUnprocessedMessages : Testing.Core.Context
    {
        private readonly DbContextCreator _context;
        private readonly BrokeredMessageOutbox<DbContext> _sut;
        private readonly Mock<ILogger<BrokeredMessageOutbox<DbContext>>> _logger;
        public WhenGettingUnprocessedMessages()
        {
            _context = New.MessageBrokers().DbContext();
            _logger = new Mock<ILogger<BrokeredMessageOutbox<DbContext>>>();
            _sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object, new ReliabilityOptions());
        }

        [Fact]
        public async void MustNotGetMessagesThatAreProcessed()
        {
            var message = New.MessageBrokers().OutboxMessage();
            _context.ThatHasOutboxMessage(message);
            var messages = await _sut.GetUnprocessedMessagesFromOutbox();
            messages.Should().BeEmpty();
        }

        [Fact]
        public async void MustGetMessagesThatAreNotProcessed()
        {
            var message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            _context.ThatHasOutboxMessage(message);
            var messages = await _sut.GetUnprocessedMessagesFromOutbox();
            messages.Should().Contain(message);
        }
    }
}
