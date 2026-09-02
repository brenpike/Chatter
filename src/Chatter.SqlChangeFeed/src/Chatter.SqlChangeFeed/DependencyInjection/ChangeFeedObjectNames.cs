using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.Scripts;
using System;

namespace Chatter.SqlChangeFeed.DependencyInjection
{
    /// <summary>
    /// Thrown when two of the four SQL Service Broker object names derived by <see cref="ChangeFeedObjectNames"/>
    /// that share the same catalog namespace (both queues, or both services) resolve to the same value. A
    /// collision lets a pre-existing or misconfigured object silently stand in for the one Chatter intends to
    /// create — e.g. a configured dead-letter service name equal to the derived conversation service name
    /// routes exhausted messages back onto the main feed queue instead of dead-lettering them.
    /// </summary>
    public class ChangeFeedObjectNameCollisionException : Exception
    {
        public ChangeFeedObjectNameCollisionException(string firstPropertyName, string secondPropertyName, string collidingName)
            : base($"'{firstPropertyName}' and '{secondPropertyName}' must be distinct Service Broker object names, but both resolved to '{collidingName}'.")
        { }
    }

    /// <summary>
    /// The single derivation of the SQL Service Broker object names that a Change Feed Migration installs and
    /// that the change feed receiver binds to. Names configured via <see cref="SqlChangeFeedOptions"/> win where
    /// supplied; every other name is derived from the row changed data type.
    /// </summary>
    internal sealed class ChangeFeedObjectNames
    {
        public string ConversationQueueName { get; }
        public string ConversationServiceName { get; }
        public string ConversationDeadLetterQueueName { get; }
        public string ConversationDeadLetterServiceName { get; }
        public string ConversationTriggerName { get; }
        public string InstallChangeFeedStoredProcName { get; }
        public string UninstallChangeFeedStoredProcName { get; }

        /// <summary>
        /// Derives the change feed object names for <paramref name="rowChangedDataType"/>, honouring any names
        /// configured on <paramref name="options"/>.
        /// </summary>
        /// <param name="rowChangedDataType">The row type the change feed is configured for</param>
        /// <param name="options">The change feed options carrying any configured names. May be null.</param>
        /// <returns><see cref="ChangeFeedObjectNames"/></returns>
        internal static ChangeFeedObjectNames DeriveFrom(Type rowChangedDataType, SqlChangeFeedOptions options)
        {
            if (rowChangedDataType is null)
            {
                throw new ArgumentNullException(nameof(rowChangedDataType));
            }

            return new ChangeFeedObjectNames(rowChangedDataType, options);
        }

        private ChangeFeedObjectNames(Type rowChangedDataType, SqlChangeFeedOptions options)
        {
            var receiverName = rowChangedDataType.Name;

            // INVARIANT: the Change Feed Trigger routes to the conversation SERVICE, never to the queue, so a
            // configured queue name replaces ConversationQueueName while ConversationServiceName stays derived.
            // The derived service is created ON the configured queue and the receiver reads that same queue.
            ConversationQueueName = string.IsNullOrWhiteSpace(options?.ChangeFeedQueueName)
                ? $"{ChatterServiceBrokerConstants.ChatterQueuePrefix}{receiverName}"
                : options.ChangeFeedQueueName;
            ConversationDeadLetterServiceName = string.IsNullOrWhiteSpace(options?.ChangeFeedDeadLetterServiceName)
                ? $"{ChatterServiceBrokerConstants.ChatterDeadLetterServicePrefix}{receiverName}"
                : options.ChangeFeedDeadLetterServiceName;
            ConversationServiceName = $"{ChatterServiceBrokerConstants.ChatterServicePrefix}{receiverName}";
            ConversationDeadLetterQueueName = $"{ChatterServiceBrokerConstants.ChatterDeadLetterQueuePrefix}{receiverName}";
            ConversationTriggerName = $"{ChatterServiceBrokerConstants.ChatterTriggerPrefix}{receiverName}";
            InstallChangeFeedStoredProcName = $"{ChatterServiceBrokerConstants.ChatterInstallChangeFeedPrefix}{receiverName}";
            UninstallChangeFeedStoredProcName = $"{ChatterServiceBrokerConstants.ChatterUninstallChangeFeedPrefix}{receiverName}";

            // INVARIANT: distinctness is checked WITHIN catalog class only, not across all four names. The two
            // queues (ConversationQueueName, ConversationDeadLetterQueueName) are schema-scoped Service Broker
            // objects and the two services (ConversationServiceName, ConversationDeadLetterServiceName) are
            // database-scoped; a queue and a service can share a literal name without colliding in the catalog,
            // so checking across classes would reject configurations that are actually safe. The Change Feed
            // Trigger and the two Change Feed Stored Procedures are excluded from this check entirely: they are
            // not Service Broker objects, and neither is independently configurable, so they can never be set
            // to collide with a queue or service name.
            RequireDistinct(nameof(ConversationQueueName), ConversationQueueName, nameof(ConversationDeadLetterQueueName), ConversationDeadLetterQueueName);
            RequireDistinct(nameof(ConversationServiceName), ConversationServiceName, nameof(ConversationDeadLetterServiceName), ConversationDeadLetterServiceName);
        }

        private static void RequireDistinct(string firstPropertyName, string firstValue, string secondPropertyName, string secondValue)
        {
            if (string.Equals(firstValue, secondValue, StringComparison.Ordinal))
            {
                throw new ChangeFeedObjectNameCollisionException(firstPropertyName, secondPropertyName, firstValue);
            }
        }
    }
}
