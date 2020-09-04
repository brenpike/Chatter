using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Saga;
using System;

namespace Chatter.MessageBrokers.Routing.Options
{
    public class SendOptions : RoutingOptions
    {
        public SendOptions()
        {}

        public SendOptions WithTransactionMode(TransactionMode transactionMode)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.TransactionMode, (byte)transactionMode);
            return this;
        }

        internal SendOptions WithSagaStatus(SagaStatusEnum sagaStatus)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.SagaStatus, (byte)sagaStatus);
            return this;
        }

        public SendOptions WithSagaId(string sagaId)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.SagaId, sagaId);
            return this;
        }

        public SendOptions WithSubject(string subject)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.Subject, subject);
            return this;
        }

        public SendOptions WithGroupId(string groupId)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.GroupId, groupId);
            return this;
        }

        public SendOptions WithTimeToLiveInMinutes(int minutes)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.TimeToLive, TimeSpan.FromMinutes(minutes));
            return this;
        }

        public void SetReplyToAddress(string replyTo)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.ReplyToAddress, replyTo);
        }

        public void SetReplyToGroupId(string replyToGroupId)
        {
            this.SetApplicationProperty(MessageBrokers.ApplicationProperties.ReplyToGroupId, replyToGroupId);
        }
    }
}
