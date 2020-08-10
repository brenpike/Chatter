using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Context;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    class BrokeredMessageDispatcher : IBrokeredMessageDispatcher
    {
        private readonly IRouteMessages _messageRouter;
        private readonly IForwardMessages _forwardRouter;
        private readonly IRouteReplyToMessages _replyToRouter;
        private readonly IBrokeredMessageDetailProvider _brokeredMessageDetailProvider;
        private readonly IRouteCompensationMessages _compensationRouter;

        public BrokeredMessageDispatcher(IRouteMessages messageRouter,
                                         IForwardMessages forwardRouter,
                                         IRouteReplyToMessages replyToRouter,
                                         IBrokeredMessageDetailProvider brokeredMessageDetailProvider,
                                         IRouteCompensationMessages compensationRouter)
        {
            _messageRouter = messageRouter ?? throw new ArgumentNullException(nameof(messageRouter));
            _forwardRouter = forwardRouter ?? throw new ArgumentNullException(nameof(forwardRouter));
            _replyToRouter = replyToRouter ?? throw new ArgumentNullException(nameof(replyToRouter));
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
            _compensationRouter = compensationRouter ?? throw new ArgumentNullException(nameof(compensationRouter));
        }

        public Task Compensate(InboundBrokeredMessage inboundBrokeredMessage, CompensationRoutingContext routingContext, TransactionContext transactionContext = null)
        {
            return _compensationRouter.Route(inboundBrokeredMessage, transactionContext, routingContext);
        }

        public Task Forward(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext = null)
        {
            return _forwardRouter.Route(inboundBrokeredMessage, forwardDestination, transactionContext);
        }

        public Task Forward(InboundBrokeredMessage inboundBrokeredMessage, IContainRoutingContext routingContext, TransactionContext transactionContext = null)
        {
            return this.Forward(inboundBrokeredMessage, routingContext.DestinationPath, transactionContext);
        }

        public Task Publish<TMessage>(TMessage message, TransactionContext transactionContext = null) where TMessage : IEvent
        {
            var destinationPath = _brokeredMessageDetailProvider.GetMessageName<TMessage>();
            return this.Publish(message, destinationPath, transactionContext);
        }

        public Task Publish<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null) where TMessage : IEvent
        {
            return _messageRouter.Route(message, destinationPath, transactionContext);
        }

        public async Task Publish<TMessage>(IEnumerable<TMessage> messages, TransactionContext transactionContext = null) where TMessage : IEvent
        {
            foreach (var message in messages)
            {
                await this.Publish(message, transactionContext).ConfigureAwait(false);
            }
        }

        public Task ReplyTo(InboundBrokeredMessage inboundBrokeredMessage, ReplyToRoutingContext routingContext, TransactionContext transactionContext = null)
        {
            return _replyToRouter.Route(inboundBrokeredMessage, transactionContext, routingContext);
        }

        public Task ReplyToRequester<TMessage>(TMessage message, TransactionContext transactionContext = null) where TMessage : ICommand
        {
            //TODO: implement
            throw new NotImplementedException();
        }

        public Task Send<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null) where TMessage : ICommand
        {
            return _messageRouter.Route(message, destinationPath, transactionContext);
        }

        public Task Send<TMessage>(TMessage message, TransactionContext transactionContext = null) where TMessage : ICommand
        {
            var destinationPath = _brokeredMessageDetailProvider.GetMessageName<TMessage>();
            return this.Send(message, destinationPath, transactionContext);
        }
    }
}
