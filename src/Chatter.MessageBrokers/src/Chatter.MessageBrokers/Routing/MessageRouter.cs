using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public sealed class MessageRouter : IRouteMessages
    {
        private readonly IBrokeredMessageInfrastructureDispatcher _brokeredMessageInfrastructureDispatcher;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IBrokeredMessageDetailProvider _brokeredMessageDetailProvider;

        public MessageRouter(IBrokeredMessageInfrastructureDispatcher brokeredMessageInfrastructureDispatcher,
                             IBodyConverterFactory bodyConverterFactory,
                             IBrokeredMessageDetailProvider brokeredMessageDetailProvider)
        {
            _brokeredMessageInfrastructureDispatcher = brokeredMessageInfrastructureDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageInfrastructureDispatcher));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
        }

        public Task Route<TMessage, TOptions>(TMessage message, TransactionContext transactionContext, TOptions options)
            where TMessage : IMessage
            where TOptions : RoutingOptions, new()
        {
            var destination = _brokeredMessageDetailProvider.GetMessageName<TMessage>();

            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new ArgumentNullException(nameof(destination), $"Routing destination is required. Use {typeof(BrokeredMessageAttribute).Name} or overload that accepts 'destinationPath'");
            }

            return this.Route(message, _brokeredMessageDetailProvider.GetMessageName<TMessage>(), transactionContext, options);
        }

        public Task Route<TMessage, TOptions>(TMessage message, string destinationPath, TransactionContext transactionContext, TOptions options)
            where TMessage : IMessage
            where TOptions : RoutingOptions, new()
        {
            if (options == null)
            {
                options = new TOptions();
            }

            if (string.IsNullOrWhiteSpace(options.ContentType))
            {
                throw new ArgumentNullException(nameof(options.ContentType), "Message content type is required");
            }

            var converter = _bodyConverterFactory.CreateBodyConverter(options.ContentType);

            var outbound = new OutboundBrokeredMessage(options.MessageId, message, options.ApplicationProperties, destinationPath, converter);

            return this.Route(outbound, transactionContext);
        }

        /// <summary>
        /// Routes an <see cref="OutboundBrokeredMessage"/> to the receiver via the message broker infrastructure.
        /// </summary>
        /// <param name="outboundBrokeredMessage">The outbound brokered message to be routed to the destination receiver</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            if (string.IsNullOrWhiteSpace(outboundBrokeredMessage.Destination))
            {
                throw new ArgumentNullException(nameof(outboundBrokeredMessage.Destination), $"Unable to route message with no destination path specified");
            }

            return _brokeredMessageInfrastructureDispatcher.Dispatch(outboundBrokeredMessage, transactionContext);
        }

        /// <summary>
        /// Routes a batch of <see cref="OutboundBrokeredMessage"/> to their receivers via the message broker infrastructure.
        /// </summary>
        /// <param name="outboundBrokeredMessages">The outbound brokered messages to be routed to the destination receivers</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(IList<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext)
            => _brokeredMessageInfrastructureDispatcher.Dispatch(outboundBrokeredMessages, transactionContext);
    }
}