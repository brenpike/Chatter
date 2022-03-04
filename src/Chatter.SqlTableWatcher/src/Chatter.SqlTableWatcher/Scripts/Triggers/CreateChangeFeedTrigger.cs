using Chatter.MessageBrokers.SqlServiceBroker;
using System;
using System.Text;

namespace Chatter.SqlTableWatcher.Scripts.Triggers
{
    /// <summary>
    /// Creates the trigger on the target table that will send changes to a SQL Service Broker CONVERSATION
    /// </summary>
    public class CreateChangeFeedTrigger
    {
        private readonly string _changeFeedTableName;
        private readonly string _changeFeedTriggerName;
        private readonly string _changeFeedChangeType;
        private readonly string _conversationServiceName;
        private readonly string _schemaName;

        /// <summary>
        /// Creates the trigger on the target table that will send changes made to the table as message to the change feed queue
        /// </summary>
        /// <param name="changeFeedTableName">The table to install the change feed</param>
        /// <param name="changeFeedTriggerName">The name of the change feed trigger to create</param>
        /// <param name="triggerRaiseByTypes">The criteria that will raise the trigger</param>
        /// <param name="conversationServiceName">The SQL Service Broker SERVICE that will be part of the COVERSATION</param>
        /// <param name="schemaName">The schema</param>
        public CreateChangeFeedTrigger(string changeFeedTableName,
                                         string changeFeedTriggerName,
                                         ChangeTypes triggerRaiseByTypes,
                                         string conversationServiceName,
                                         string schemaName)
        {
            if (string.IsNullOrWhiteSpace(changeFeedTableName))
            {
                throw new ArgumentException($"'{nameof(changeFeedTableName)}' cannot be null or whitespace", nameof(changeFeedTableName));
            }

            if (string.IsNullOrWhiteSpace(changeFeedTriggerName))
            {
                throw new ArgumentException($"'{nameof(changeFeedTriggerName)}' cannot be null or whitespace", nameof(changeFeedTriggerName));
            }

            if (string.IsNullOrWhiteSpace(conversationServiceName))
            {
                throw new ArgumentException($"'{nameof(conversationServiceName)}' cannot be null or whitespace", nameof(conversationServiceName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            _changeFeedTableName = changeFeedTableName;
            _changeFeedTriggerName = changeFeedTriggerName;
            _conversationServiceName = conversationServiceName;
            _schemaName = schemaName;
            _changeFeedChangeType = GetTriggerAfterStatementCriteria(triggerRaiseByTypes);
        }

        private string GetTriggerAfterStatementCriteria(ChangeTypes types)
        {
            StringBuilder result = new StringBuilder();
            if (types.HasFlag(ChangeTypes.Insert))
                result.Append("INSERT");
            if (types.HasFlag(ChangeTypes.Update))
                result.Append(result.Length == 0 ? "UPDATE" : ", UPDATE");
            if (types.HasFlag(ChangeTypes.Delete))
                result.Append(result.Length == 0 ? "DELETE" : ", DELETE");
            if (result.Length == 0) result.Append("INSERT");

            return result.ToString();
        }

        public override string ToString()
        {
            return string.Format(@"
                CREATE TRIGGER {4}.[{1}]
                ON {4}.[{0}]
                WITH EXECUTE AS OWNER
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

                    IF @message IS NOT NULL
					BEGIN
                        SET @message = compress(@message)                    

                	    DECLARE @ConvHandle UNIQUEIDENTIFIER

                	    BEGIN DIALOG @ConvHandle 
                            FROM SERVICE [{3}] TO SERVICE '{3}' ON CONTRACT [{5}] WITH ENCRYPTION=OFF; 

                        SEND ON CONVERSATION @ConvHandle MESSAGE TYPE [DEFAULT] (@message);
                    END
                END
            ", _changeFeedTableName, _changeFeedTriggerName, _changeFeedChangeType, _conversationServiceName, _schemaName, ServicesMessageTypes.ChatterServiceContract);
        }
    }
}
