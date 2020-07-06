using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Context
{
    public sealed class MessageBrokerContext : MessageHandlerContext, IMessageBrokerContext
    {
        public MessageBrokerContext(string messageId, byte[] body, IDictionary<string, object> applicationProperties, string messageReceiverPath, IBrokeredMessageBodyConverter bodyConverter)
        {
            this.BrokeredMessage = new InboundBrokeredMessage(messageId, body, applicationProperties, messageReceiverPath, bodyConverter);
        }

        public MessageBrokerContext(InboundBrokeredMessage brokeredMessage)
        {
            this.BrokeredMessage = brokeredMessage;
        }

        public InboundBrokeredMessage BrokeredMessage { get; private set; }
        public INextDestinationRouter NextDestinationRouter { get; internal set; }
        public IReplyRouter ReplyRouter { get; internal set; }
        public ICompensateRouter CompensateRouter { get; internal set; }

        public void SetError(ErrorContext errorContext)
        {
            this.Container.Set(errorContext);
            this.BrokeredMessage.SetError();
            this.BrokeredMessage.WithFailureDetails(errorContext.ErrorDetails);
            this.BrokeredMessage.WithFailureDescription(errorContext.ErrorDescription);
        }
    }
}
