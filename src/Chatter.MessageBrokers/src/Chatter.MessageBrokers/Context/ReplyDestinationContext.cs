using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    public sealed class ReplyDestinationContext : DestinationRouterContext
    {
        public ReplyDestinationContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, string replyToGroupId, ContextContainer container = null)
            : base(destinationPath, destinationMessageCreator, container)
        {
            ReplyToGroupId = replyToGroupId;
        }

        public string ReplyToGroupId { get; set; } = null;

        public ReplyDestinationContext SetDestinationMessageCreator(Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator)
        {
            return new ReplyDestinationContext(this.DestinationPath, destinationMessageCreator, this.ReplyToGroupId, this.Container);
        }
    }
}
