using System;

namespace Chatter.MessageBrokers.Routing.Options
{
    public class SendOptions : RoutingOptions
    {
        public SendOptions()
        {}

        public SendOptions WithSagaId(string sagaId)
        {
            this.WithMessageContext(MessageBrokers.MessageContext.SagaId, sagaId);
            return this;
        }

        public SendOptions WithSubject(string subject)
        {
            this.WithMessageContext(MessageBrokers.MessageContext.Subject, subject);
            return this;
        }

        public SendOptions WithGroupId(string groupId)
        {
            this.WithMessageContext(MessageBrokers.MessageContext.GroupId, groupId);
            return this;
        }

        public SendOptions WithTimeToLiveInMinutes(int minutes)
        {
            this.WithMessageContext(MessageBrokers.MessageContext.TimeToLive, TimeSpan.FromMinutes(minutes));
            return this;
        }

        public void SetReplyToAddress(string replyTo)
        {
            this.WithMessageContext(MessageBrokers.MessageContext.ReplyToAddress, replyTo);
        }

        public void SetReplyToGroupId(string replyToGroupId)
        {
            this.WithMessageContext(MessageBrokers.MessageContext.ReplyToGroupId, replyToGroupId);
        }
    }
}
