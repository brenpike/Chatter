using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Routing.Options;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    class BrokeredMessageDispatcher : IBrokeredMessageDispatcher
    {
        private readonly IRouteMessages _messageRouter;
        private readonly IForwardMessages _forwardRouter;
        private readonly IReplyRouter _replyToRouter;
        private readonly IRouteCompensationMessages _compensationRouter;

        public BrokeredMessageDispatcher(IRouteMessages messageRouter,
                                         IForwardMessages forwardRouter,
                                         IReplyRouter replyToRouter,
                                         IRouteCompensationMessages compensationRouter)
        {
            _messageRouter = messageRouter ?? throw new ArgumentNullException(nameof(messageRouter));
            _forwardRouter = forwardRouter ?? throw new ArgumentNullException(nameof(forwardRouter));
            _replyToRouter = replyToRouter ?? throw new ArgumentNullException(nameof(replyToRouter));
            _compensationRouter = compensationRouter ?? throw new ArgumentNullException(nameof(compensationRouter));
        }

        public Task Send<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand 
            => _messageRouter.Route(message, destinationPath, transactionContext, options ?? new SendOptions());

        public Task Send<TMessage>(TMessage message, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand
            => _messageRouter.Route(message, transactionContext, options ?? new SendOptions());

        public Task Publish<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
            => _messageRouter.Route(message, destinationPath, transactionContext, options ?? new PublishOptions());

        public Task Publish<TMessage>(TMessage message, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
            => _messageRouter.Route(message, transactionContext, options ?? new PublishOptions());

        public Task Compensate(InboundBrokeredMessage inboundBrokeredMessage, CompensationRoutingContext routingContext, TransactionContext transactionContext = null)
            => _compensationRouter.Route(inboundBrokeredMessage, transactionContext, routingContext);

        public Task Forward(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext = null, ForwardingOptions options = null) 
            => _forwardRouter.Route(inboundBrokeredMessage, forwardDestination, transactionContext);

        public Task Forward(InboundBrokeredMessage inboundBrokeredMessage, IContainRoutingContext routingContext, TransactionContext transactionContext = null, ForwardingOptions options = null)
        {
            if (routingContext is null)
            {
                //log
                return Task.CompletedTask;
            }

            return this.Forward(inboundBrokeredMessage, routingContext.DestinationPath, transactionContext, options);
        }

        public Task ReplyTo(InboundBrokeredMessage inboundBrokeredMessage, ReplyToRoutingContext routingContext, TransactionContext transactionContext = null, ReplyToOptions options = null)
        {
            if (routingContext is null)
            {
                //log
                return Task.CompletedTask;
            }

            return _replyToRouter.Route(inboundBrokeredMessage, transactionContext, routingContext);
        }

        public Task ReplyToRequester<TMessage>(TMessage message, TransactionContext transactionContext = null) where TMessage : ICommand
        {
            throw new NotImplementedException();
        }
    }
}
