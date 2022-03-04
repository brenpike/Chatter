using Chatter.CQRS;
using Chatter.CQRS.Events;

namespace Chatter.SqlChangeFeed
{
    public class RowInsertedEvent<TRowChangeData> : IEvent where TRowChangeData : class, IMessage
    {
        public RowInsertedEvent(TRowChangeData inserted) => Inserted = inserted;
        public TRowChangeData Inserted { get; internal set; }
    }
}
