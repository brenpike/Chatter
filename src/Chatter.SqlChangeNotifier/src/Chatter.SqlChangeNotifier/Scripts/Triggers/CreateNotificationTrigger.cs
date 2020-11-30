using System;
using System.Text;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts.Triggers
{
    /// <summary>
    /// Creates the trigger on the target table that will send changes to a SQL Service Broker CONVERSATION
    /// </summary>
    public class CreateNotificationTrigger
    {
        private readonly string _monitorableTableName;
        private readonly string _notificationTriggerName;
        private readonly string _notificationTriggeredBy;
        private readonly string _conversationServiceName;
        private readonly string _schemaName;

        /// <summary>
        /// Creates the trigger on the target table that will send
        /// </summary>
        /// <param name="monitorableTableName">The table to monitor for changes</param>
        /// <param name="notificationTriggerName">The name of the notification trigger to create</param>
        /// <param name="triggerRaiseByTypes">The criteria that will raise the trigger</param>
        /// <param name="conversationServiceName">The SQL Service Broker SERVICE that will be part of the COVERSATION</param>
        /// <param name="schemaName">The schema</param>
        public CreateNotificationTrigger(string monitorableTableName,
                                         string notificationTriggerName,
                                         NotificationTypes triggerRaiseByTypes,
                                         string conversationServiceName,
                                         string schemaName)
        {
            if (string.IsNullOrWhiteSpace(monitorableTableName))
            {
                throw new ArgumentException($"'{nameof(monitorableTableName)}' cannot be null or whitespace", nameof(monitorableTableName));
            }

            if (string.IsNullOrWhiteSpace(notificationTriggerName))
            {
                throw new ArgumentException($"'{nameof(notificationTriggerName)}' cannot be null or whitespace", nameof(notificationTriggerName));
            }

            if (string.IsNullOrWhiteSpace(conversationServiceName))
            {
                throw new ArgumentException($"'{nameof(conversationServiceName)}' cannot be null or whitespace", nameof(conversationServiceName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            _monitorableTableName = monitorableTableName;
            _notificationTriggerName = notificationTriggerName;
            _conversationServiceName = conversationServiceName;
            _schemaName = schemaName;
            _notificationTriggeredBy = GetTriggerAfterStatementCriteria(triggerRaiseByTypes);
        }

        private string GetTriggerAfterStatementCriteria(NotificationTypes types)
        {
            StringBuilder result = new StringBuilder();
            if (types.HasFlag(NotificationTypes.Insert))
                result.Append("INSERT");
            if (types.HasFlag(NotificationTypes.Update))
                result.Append(result.Length == 0 ? "UPDATE" : ", UPDATE");
            if (types.HasFlag(NotificationTypes.Delete))
                result.Append(result.Length == 0 ? "DELETE" : ", DELETE");
            if (result.Length == 0) result.Append("INSERT");

            return result.ToString();
        }

        public override string ToString()
        {
            return string.Format(@"
                CREATE TRIGGER [{1}]
                ON {4}.[{0}]
                AFTER {2} 
                AS

                SET NOCOUNT ON;

                IF EXISTS (SELECT * FROM sys.services WHERE name = '{3}')
                BEGIN
                    DECLARE @message NVARCHAR(MAX)
                    SET @message = N''

                    DECLARE @InsertedJSON NVARCHAR(MAX) 
                    DECLARE @DeletedJSON NVARCHAR(MAX) 
                    
                    %inserted_select_statement%
                    
                    %deleted_select_statement% 
                    
                    IF (COALESCE(@DeletedJSON, N'') = N'') SET @message = @InsertedJSON
                    ELSE
                    	IF (COALESCE(@InsertedJSON, N'') = N'') SET @message = @DeletedJSON
                    ELSE
                    	SET @message = CONCAT(SUBSTRING(@InsertedJSON,1,LEN(@InsertedJSON) - 1), N',', SUBSTRING(@DeletedJSON,2,LEN(@DeletedJSON)-1))

                    SET @message = compress(@message)                    

                	DECLARE @ConvHandle UNIQUEIDENTIFIER

                	BEGIN DIALOG @ConvHandle 
                        FROM SERVICE [{3}] TO SERVICE '{3}' ON CONTRACT [DEFAULT] WITH ENCRYPTION=OFF, LIFETIME = 60; 

                 SEND ON CONVERSATION @ConvHandle MESSAGE TYPE [DEFAULT] (@message);

                 END CONVERSATION @ConvHandle;
                END
            ", _monitorableTableName, _notificationTriggerName, _notificationTriggeredBy, _conversationServiceName, _schemaName);
        }
    }
}
