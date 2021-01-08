using System;

namespace Chatter.SqlTableWatcher.Scripts.Triggers
{
    /// <summary>
    /// Deletes the notification trigger
    /// </summary>
    public class DeleteNotificationTrigger
    {
        private readonly string _notificationTriggerName;
        private readonly string _schemaName;

        /// <summary>
        /// Deletes the notification trigger
        /// </summary>
        /// <param name="notificationTriggerName">The name of the notification trigger to delete</param>
        /// <param name="schemaName">The schema</param>
        public DeleteNotificationTrigger(string notificationTriggerName, string schemaName)
        {
            if (string.IsNullOrWhiteSpace(notificationTriggerName))
            {
                throw new ArgumentException($"'{nameof(notificationTriggerName)}' cannot be null or whitespace", nameof(notificationTriggerName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            _notificationTriggerName = notificationTriggerName;
            _schemaName = schemaName;
        }

        public override string ToString()
        {
            return string.Format(@"
                IF OBJECT_ID ('{1}.{0}', 'TR') IS NOT NULL
                    DROP TRIGGER {1}.[{0}];
            ", _notificationTriggerName, _schemaName);
        }
    }
}
