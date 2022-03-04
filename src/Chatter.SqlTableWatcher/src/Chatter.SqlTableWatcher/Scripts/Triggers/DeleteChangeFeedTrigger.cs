using System;

namespace Chatter.SqlTableWatcher.Scripts.Triggers
{
    /// <summary>
    /// Deletes the change feed trigger
    /// </summary>
    public class DeleteChangeFeedTrigger
    {
        private readonly string _changeFeedTriggerName;
        private readonly string _schemaName;

        /// <summary>
        /// Deletes the change feed trigger
        /// </summary>
        /// <param name="changeFeedTriggerName">The name of the change feed trigger to delete</param>
        /// <param name="schemaName">The schema</param>
        public DeleteChangeFeedTrigger(string changeFeedTriggerName, string schemaName)
        {
            if (string.IsNullOrWhiteSpace(changeFeedTriggerName))
            {
                throw new ArgumentException($"'{nameof(changeFeedTriggerName)}' cannot be null or whitespace", nameof(changeFeedTriggerName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            _changeFeedTriggerName = changeFeedTriggerName;
            _schemaName = schemaName;
        }

        public override string ToString()
        {
            return string.Format(@"
                IF OBJECT_ID ('{1}.{0}', 'TR') IS NOT NULL
                    DROP TRIGGER {1}.[{0}];
            ", _changeFeedTriggerName, _schemaName);
        }
    }
}
