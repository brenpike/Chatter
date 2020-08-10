using Chatter.MessageBrokers.Routing.Context;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class ReplyToRoutingExceptions : BrokeredMessageRoutingException
    {
        private readonly ReplyToRoutingContext _replyToContext;

        public override RoutingContext RoutingContext => _replyToContext;

        public ReplyToRoutingExceptions(ReplyToRoutingContext replyToContext, Exception causeOfRoutingFailure)
            : base(replyToContext, causeOfRoutingFailure, "Routing 'reply to' message failed.")
        {
            _replyToContext = replyToContext ?? throw new ArgumentNullException(nameof(replyToContext), "A 'reply to' context is required.");
        }
    }
}
