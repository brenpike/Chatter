namespace Chatter.MessageBrokers.SqlServiceBroker
{
    /// <summary>
    /// SQL Server Service Broker message types as defined in sys.services_message_types
    /// </summary>
    public static class ServicesMessageTypes
    {
        /// <summary>
        /// The error message type. Created when conversation is ended with error.
        /// </summary>
        public const string ErrorType = "http://schemas.microsoft.com/SQL/ServiceBroker/Error";
        /// <summary>
        /// The message type for a message that is created when a dialog is ended without error.
        /// </summary>
        public const string EndDialogType = "http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog";
        /// <summary>
        /// The message type SQL Server sends to notify subscribers to query results of changes to these results.
        /// </summary>
        public const string QueryNotificationType = "http://schemas.microsoft.com/SQL/Notifications/QueryNotification";
        /// <summary>
        /// The message type for messages that are driggered from a DDL operation
        /// </summary>
        public const string EventNotificationType = "http://schemas.microsoft.com/SQL/Notifications/EventNotification";
        public const string DialogTimerType = "http://schemas.microsoft.com/SQL/ServiceBroker/DialogTimer";
        public const string MissingRouteType = "http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice/MissingRoute";
        public const string FailedRouteType = "http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice/FailedRoute";
        public const string MissingRemoteServiceBindingType = "http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice/MissingRemoteServiceBinding";
        public const string FailedRemoteServiceBindingType = "http://schemas.microsoft.com/SQL/ServiceBroker/BrokerConfigurationNotice/FailedRemoteServiceBinding";
        public const string EchoType = "http://schemas.microsoft.com/SQL/ServiceBroker/ServiceEcho/Echo";
        public const string QueryType = "http://schemas.microsoft.com/SQL/ServiceBroker/ServiceDiagnostic/Query";
        public const string StatusType = "http://schemas.microsoft.com/SQL/ServiceBroker/ServiceDiagnostic/Status";
        public const string DescriptionType = "http://schemas.microsoft.com/SQL/ServiceBroker/ServiceDiagnostic/Description";
        public const string DefaultType = "DEFAULT";
    }
}
