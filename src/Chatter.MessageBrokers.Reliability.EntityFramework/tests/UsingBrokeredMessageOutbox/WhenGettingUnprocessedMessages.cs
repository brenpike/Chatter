using Chatter.Testing.Core.Creators.MessageBrokers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.UsingBrokeredMessageOutbox
{
    public class WhenGettingUnprocessedMessages : Testing.Core.Context
    {
        private DbContextCreator _context;
        private readonly BrokeredMessageOutbox<DbContext> _sut;
        private readonly Mock<ILogger<BrokeredMessageOutbox<DbContext>>> _logger;

        public WhenGettingUnprocessedMessages()
        {
            _context = New.MessageBrokers().DbContext();
            _logger = new Mock<ILogger<BrokeredMessageOutbox<DbContext>>>();
            _sut = new BrokeredMessageOutbox<DbContext>(_context, _logger.Object);
        }

        [Fact]
        public async Task MustNotGetMessagesThatAreProcessed()
        {
            var message = New.MessageBrokers().OutboxMessage();
            _context.ThatHasOutboxMessage(message);
            var messages = await _sut.GetUnprocessedMessagesFromOutbox();
            messages.Should().BeEmpty();
        }

        [Fact]
        public async Task MustGetMessagesThatAreNotProcessed()
        {
            var message = New.MessageBrokers().OutboxMessage().ThatIsNotProcessed();
            _context.ThatHasOutboxMessage(message);
            var messages = await _sut.GetUnprocessedMessagesFromOutbox();
            messages.Should().Contain(message);
        }
    }
}
