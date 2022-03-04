namespace Chatter.SqlTableWatcher.Scripts
{
    internal class ChatterServiceBrokerConstants
    {
        public const string ChatterQueuePrefix = "Chatter_Queue_";
        public const string ChatterServicePrefix = "Chatter_Service_";
        public const string ChatterDeadLetterQueuePrefix = "Chatter_DeadLetterQueue_";
        public const string ChatterDeadLetterServicePrefix = "Chatter_DeadLetterService_";
        public const string ChatterTriggerPrefix = "Chatter_ChangeFeedTrigger_";
        public const string ChatterInstallChangeFeedPrefix = "Chatter_InstallChangeFeed_";
        public const string ChatterUninstallChangeFeedPrefix = "Chatter_UninstallChangeFeed_";
    }
}
