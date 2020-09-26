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
            this.SetApplicationProperty(MessageBrokers.MessageContext.TransactionMode, (byte)transactionMode);
            return this;
        }

        internal SendOptions WithSagaStatus(SagaStatusEnum sagaStatus)
        {
            this.SetApplicationProperty(MessageBrokers.MessageContext.SagaStatus, (byte)sagaStatus);
            return this;
        }

        public SendOptions WithSagaId(string sagaId)
        {
            this.SetApplicationProperty(MessageBrokers.MessageContext.SagaId, sagaId);
            return this;
        }

        public SendOptions WithSubject(string subject)
        {
            this.SetApplicationProperty(MessageBrokers.MessageContext.Subject, subject);
            return this;
        }

        public SendOptions WithGroupId(string groupId)
        {
            this.SetApplicationProperty(MessageBrokers.MessageContext.GroupId, groupId);
            return this;
        }

        public SendOptions WithTimeToLiveInMinutes(int minutes)
        {
            this.SetApplicationProperty(MessageBrokers.MessageContext.TimeToLive, TimeSpan.FromMinutes(minutes));
            return this;
        }

        public void SetReplyToAddress(string replyTo)
        {
            this.SetApplicationProperty(MessageBrokers.MessageContext.ReplyToAddress, replyTo);
        }

        public void SetReplyToGroupId(string replyToGroupId)
        {
            this.SetApplicationProperty(MessageBrokers.MessageContext.ReplyToGroupId, replyToGroupId);
        }
    }
}
