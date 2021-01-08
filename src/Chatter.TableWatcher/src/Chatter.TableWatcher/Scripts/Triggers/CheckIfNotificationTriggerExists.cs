using System;

namespace Chatter.SqlTableWatcher.Scripts.Triggers
{
    /// <summary>
    /// Checks for the existence of the notification trigger
    /// </summary>
    public class CheckIfNotificationTriggerExists
    {
        private readonly string _notificationTriggerName;
        private readonly string _schemaName;

        /// <summary>
        /// Checks for the existence of the notification trigger
        /// </summary>
        /// <param name="notificationTriggerName">The name of the notification trigger</param>
        /// <param name="schemaName">The schema where the notification exists</param>
        public CheckIfNotificationTriggerExists(string notificationTriggerName, string schemaName)
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
                    RETURN;
            ", _notificationTriggerName, _schemaName);
        }
    }
}
