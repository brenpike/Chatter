using System;

namespace Chatter.MessageBrokers.SqlServiceBroker
{
    [Flags]
    public enum NotificationTypes
    {
        None = 0,
        Insert = 1 << 1,
        Update = 1 << 2,
        Delete = 1 << 3
    }
}
