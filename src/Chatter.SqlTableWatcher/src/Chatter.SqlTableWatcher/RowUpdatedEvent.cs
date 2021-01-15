using Chatter.CQRS;
using Chatter.CQRS.Events;

namespace Chatter.SqlTableWatcher
{
    public class RowUpdatedEvent<TRowChangeData> : IEvent where TRowChangeData : class, IMessage
    {
        public RowUpdatedEvent(TRowChangeData newValue, TRowChangeData oldValue)
        {
            NewValue = newValue;
            OldValue = oldValue;
        }

        public TRowChangeData NewValue { get; internal set; }
        public TRowChangeData OldValue { get; internal set; }
    }
}
