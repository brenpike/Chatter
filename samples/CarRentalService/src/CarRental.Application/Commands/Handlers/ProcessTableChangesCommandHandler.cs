using CarRental.Application.Events;
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.SqlTableWatcher;
using System.Threading.Tasks;

namespace CarRental.Application.Commands.Handlers
{
    public class ProcessTableChangesCommandHandler : IMessageHandler<ProcessTableChangesCommand<OutboxChangedEvent>>
    {
        public Task Handle(ProcessTableChangesCommand<OutboxChangedEvent> message, IMessageHandlerContext context)
        {
            return Task.CompletedTask;
        }
    }
}
