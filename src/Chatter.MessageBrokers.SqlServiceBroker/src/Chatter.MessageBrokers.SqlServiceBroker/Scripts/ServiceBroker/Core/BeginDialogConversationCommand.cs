using System;
using System.Data;
using System.Text;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts.ServiceBroker.Core
{
    public class BeginDialogConversationCommand
    {
        private readonly IDbTransaction transaction;
        private string initiatorServiceName;
        private string targetServiceName;
        private readonly string messageContractName;
        private readonly int lifetime;
        private readonly bool encryption;
        private readonly Guid relatedConversationGroupId;
        private readonly Guid relatedConversationId;

        public BeginDialogConversationCommand(IDbTransaction transaction,
                                       string initiatorServiceName,
                                       string targetServiceName,
                                       string messageContractName,
                                       int lifetime = 0,
                                       bool encryption = false,
                                       Guid relatedConversationGroupId = default,
                                       Guid relatedConversationId = default)
        {
            this.transaction = transaction;
            this.initiatorServiceName = initiatorServiceName;
            this.targetServiceName = targetServiceName;
            this.messageContractName = messageContractName;
            this.lifetime = lifetime;
            this.encryption = encryption;
            this.relatedConversationGroupId = relatedConversationGroupId;
            this.relatedConversationId = relatedConversationId;
        }

        public override string ToString()
        {
            if (!initiatorServiceName.StartsWith("["))
            {
                initiatorServiceName = "[" + initiatorServiceName + "]";
            }

            targetServiceName = targetServiceName.Replace("]", "").Replace("[", "");

            var query = new StringBuilder();

            query.Append($"BEGIN DIALOG @conversationHandle FROM SERVICE {initiatorServiceName} TO SERVICE '@targetService' ON CONTRACT");

            if (messageContractName != null)
            {
                query.Append(" @contractName");
            }
            else
            {
                query.Append(" [DEFAULT]");
            }

            query.Append($" WITH ENCRYPTION = ");

            if (encryption)
            {
                query.Append("ON ");
            }
            else
            {
                query.Append("OFF ");
            }

            if (relatedConversationGroupId != default)
            {
                query.Append("WITH RELATED_CONVERSATION_GROUP = @conversationGroupId ");
            }

            if (relatedConversationId != default)
            {
                query.Append("WITH RELATED_CONVERSATION = @conversationId ");
            }

            if (lifetime > 0)
            {
                query.Append($" LIFETIME = {lifetime} ;");
            }

            return query.ToString();
        }
    }
}
