using Chatter.CQRS;
using Chatter.CQRS.Events;

namespace Chatter.SqlTableWatcher
{
    public class RowDeletedEvent<TRowChangeData> : IEvent where TRowChangeData : class, IMessage
    {
        public RowDeletedEvent(TRowChangeData deleted) => Deleted = deleted;
        public TRowChangeData Deleted { get; internal set; }
    }
}
