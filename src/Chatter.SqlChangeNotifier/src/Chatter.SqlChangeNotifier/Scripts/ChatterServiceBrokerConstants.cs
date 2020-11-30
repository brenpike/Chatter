using System;
using System.Collections.Generic;
using System.Text;

namespace Chatter.SqlChangeNotifier.Scripts
{
    internal class ChatterServiceBrokerConstants
    {
        public const string ChatterQueuePrefix = "Chatter_ConversationQueue_";
        public const string ChatterServicePrefix = "Chatter_ConversationService_";
        public const string ChatterTriggerPrefix = "Chatter_NotificationTrigger_";
        public const string ChatterInstallNotificationsPrefix = "Chatter_InstallNotifications_";
        public const string ChatterUninstallNotificationsPrefix = "Chatter_UninstallNotifications_";
    }
}
