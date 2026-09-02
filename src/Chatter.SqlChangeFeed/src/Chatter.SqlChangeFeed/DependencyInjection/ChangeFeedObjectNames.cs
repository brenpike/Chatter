using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.Scripts;
using System;

namespace Chatter.SqlChangeFeed.DependencyInjection
{
    /// <summary>
    /// Thrown when two of the seven SQL Service Broker object names derived by <see cref="ChangeFeedObjectNames"/>
    /// that share the same catalog namespace (the schema-scoped <c>sys.objects</c> namespace shared by both
    /// queues, the Change Feed Trigger, and both Change Feed Stored Procedures; or the database-scoped service
    /// namespace shared by both services) resolve to the same value, comparing case-insensitively to match SQL
    /// Server's default collation. A collision lets a pre-existing or misconfigured object silently stand in
    /// for the one Chatter intends to create — e.g. a configured dead-letter service name equal to the derived
    /// conversation service name routes exhausted messages back onto the main feed queue instead of
    /// dead-lettering them.
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

            // INVARIANT: distinctness is checked WITHIN catalog namespace only, never across namespaces. SQL
            // Server groups sys.service_queues (type SQ), triggers (TR), and stored procedures (P) into ONE
            // schema-scoped sys.objects namespace, so ConversationQueueName, ConversationDeadLetterQueueName,
            // ConversationTriggerName, InstallChangeFeedStoredProcName, and UninstallChangeFeedStoredProcName
            // must all be mutually distinct. Services live in their own database-scoped catalog, so
            // ConversationServiceName and ConversationDeadLetterServiceName are checked only against each
            // other; a queue and a service CAN share a literal name without colliding in the catalog, so
            // checking across namespaces would reject configurations that are actually safe.
            //
            // Only ConversationQueueName and ConversationDeadLetterServiceName are configurable — every other
            // name is derived from a fixed, distinct prefix plus the same receiver name, so those five can
            // never collide with each other by construction. That leaves exactly the checks below: each
            // configurable name against every fixed name that shares its namespace, plus the two configurable
            // names against each other where they share a namespace (they don't — one is a queue, one is a
            // service).
            //
            // INVARIANT: comparisons use OrdinalIgnoreCase, not Ordinal. SQL Server's default collation is
            // case-insensitive, so two names differing only in case collide in the database even though they
            // pass an Ordinal check. This module has no visibility into the target server's actual collation,
            // so OrdinalIgnoreCase is the conservative choice: a false rejection is a loud, easily-resolved
            // startup error, whereas a false acceptance under Ordinal is exactly the silent database collision
            // this check exists to prevent.
            RequireDistinct(nameof(ConversationQueueName), ConversationQueueName, nameof(ConversationDeadLetterQueueName), ConversationDeadLetterQueueName);
            RequireDistinct(nameof(ConversationQueueName), ConversationQueueName, nameof(ConversationTriggerName), ConversationTriggerName);
            RequireDistinct(nameof(ConversationQueueName), ConversationQueueName, nameof(InstallChangeFeedStoredProcName), InstallChangeFeedStoredProcName);
            RequireDistinct(nameof(ConversationQueueName), ConversationQueueName, nameof(UninstallChangeFeedStoredProcName), UninstallChangeFeedStoredProcName);
            RequireDistinct(nameof(ConversationServiceName), ConversationServiceName, nameof(ConversationDeadLetterServiceName), ConversationDeadLetterServiceName);
        }

        private static void RequireDistinct(string firstPropertyName, string firstValue, string secondPropertyName, string secondValue)
        {
            if (string.Equals(firstValue, secondValue, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChangeFeedObjectNameCollisionException(firstPropertyName, secondPropertyName, firstValue);
            }
        }
    }
}
