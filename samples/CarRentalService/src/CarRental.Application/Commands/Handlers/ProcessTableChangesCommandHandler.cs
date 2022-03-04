using CarRental.Application.Events;
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.SqlChangeFeed;
using System.Threading.Tasks;

namespace CarRental.Application.Commands.Handlers
{
    public class ProcessTableChangesCommandHandler : IMessageHandler<ProcessChangeFeedCommand<OutboxChangedEvent>>
    {
        public Task Handle(ProcessChangeFeedCommand<OutboxChangedEvent> message, IMessageHandlerContext context)
        {
            return Task.CompletedTask;
        }
    }
}
