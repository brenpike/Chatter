using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    class BrokeredMessageDispatcher : IBrokeredMessageDispatcher
    {
        private readonly IRouteBrokeredMessages _messageRouter;
        private readonly IBrokeredMessageDetailProvider _brokeredMessageDetailProvider;
        private readonly IBodyConverterFactory _bodyConverterFactory;

        public BrokeredMessageDispatcher(IRouteBrokeredMessages messageRouter,
                                         IBrokeredMessageDetailProvider brokeredMessageDetailProvider,
                                         IBodyConverterFactory bodyConverterFactory)
        {
            _messageRouter = messageRouter ?? throw new ArgumentNullException(nameof(messageRouter));
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
        }

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand 
            => Dispatch(message, destinationPath, transactionContext, options ?? new SendOptions());

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand 
            => Dispatch(message, transactionContext, options ?? new SendOptions());

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent 
            => Dispatch(message, destinationPath, transactionContext, options ?? new PublishOptions());

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent 
            => Dispatch(message, transactionContext, options ?? new PublishOptions());

        //TODO: add replytorequester that takes a TMessage and uses the ApplicationProperty RequesterPath to reply to

        Task Dispatch<TMessage, TOptions>(TMessage message, TransactionContext transactionContext, TOptions options)
            where TMessage : IMessage
            where TOptions : RoutingOptions, new()
        {
            var destination = _brokeredMessageDetailProvider.GetMessageName(message.GetType());

            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new ArgumentNullException(nameof(destination), $"Routing destination is required. Use {typeof(BrokeredMessageAttribute).Name} or overload that accepts 'destinationPath'");
            }

            return this.Dispatch(message, destination, transactionContext, options);
        }

        Task Dispatch<TMessage, TOptions>(TMessage message, string destinationPath, TransactionContext transactionContext, TOptions options)
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

            return _messageRouter.Route(outbound, transactionContext);
        }
    }
}
