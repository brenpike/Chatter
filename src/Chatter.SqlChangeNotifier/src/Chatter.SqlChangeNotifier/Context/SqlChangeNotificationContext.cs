using Chatter.CQRS.Events;

namespace Chatter.SqlChangeNotifier.Context
{
    public class SqlChangeNotificationContext<TOldValue> where TOldValue : class, IEvent
    {
        private SqlChangeNotificationContext(ChangeType changeType, TOldValue oldValue)
        {
            ChangeType = changeType;
            OldValue = oldValue;
        }

        public static SqlChangeNotificationContext<T> Create<T>(ChangeType changeType, T oldValue = null) where T : class, IEvent
        {
            return new SqlChangeNotificationContext<T>(changeType, oldValue);
        }

        public ChangeType ChangeType { get; }
        public TOldValue OldValue { get; }
    }
}
