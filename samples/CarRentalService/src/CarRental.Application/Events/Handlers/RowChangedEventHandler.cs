using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.SqlTableWatcher;
using System.Threading.Tasks;

namespace CarRental.Application.Events.Handlers
{
    public class RowChangedEventHandler : IMessageHandler<RowUpdatedEvent<OutboxChangedEvent>>,
                                          IMessageHandler<RowInsertedEvent<OutboxChangedEvent>>,
                                          IMessageHandler<RowDeletedEvent<OutboxChangedEvent>>
    {
        public Task Handle(RowUpdatedEvent<OutboxChangedEvent> message, IMessageHandlerContext context)
        {
            return Task.CompletedTask;
        }

        public Task Handle(RowInsertedEvent<OutboxChangedEvent> message, IMessageHandlerContext context)
        {
            return Task.CompletedTask;
        }

        public Task Handle(RowDeletedEvent<OutboxChangedEvent> message, IMessageHandlerContext context)
        {
            return Task.CompletedTask;
        }
    }
}
