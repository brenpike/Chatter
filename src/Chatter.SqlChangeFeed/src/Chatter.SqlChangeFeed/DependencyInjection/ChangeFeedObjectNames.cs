using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.Scripts;
using System;

namespace Chatter.SqlChangeFeed.DependencyInjection
{
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
        }
    }
}
