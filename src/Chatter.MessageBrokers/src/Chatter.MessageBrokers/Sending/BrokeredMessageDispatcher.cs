﻿using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    class BrokeredMessageDispatcher : IBrokeredMessageDispatcher
    {
        private readonly IRouteBrokeredMessages _messageRouter;
        private readonly IForwardMessages _forwarder;
        private readonly IBrokeredMessageAttributeDetailProvider _brokeredMessageDetailProvider;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IMessageIdGenerator _messageIdGenerator;

        public BrokeredMessageDispatcher(IRouteBrokeredMessages messageRouter,
                                         IForwardMessages forwarder,
                                         IBrokeredMessageAttributeDetailProvider brokeredMessageDetailProvider,
                                         IBodyConverterFactory bodyConverterFactory,
                                         IMessageIdGenerator messageIdGenerator)
        {
            _messageRouter = messageRouter ?? throw new ArgumentNullException(nameof(messageRouter));
            _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _messageIdGenerator = messageIdGenerator ?? throw new ArgumentNullException(nameof(messageIdGenerator));
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

        public Task Forward(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext) 
            => _forwarder.Route(inboundBrokeredMessage, forwardDestination, transactionContext);

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

            OutboundBrokeredMessage outbound;

            if (string.IsNullOrWhiteSpace(options.MessageId))
            {
                outbound = new OutboundBrokeredMessage(_messageIdGenerator, message, options.MessageContext, destinationPath, converter);
            }
            else
            {
                outbound = new OutboundBrokeredMessage(options.MessageId, message, options.MessageContext, destinationPath, converter);
            }

            return _messageRouter.Route(outbound, transactionContext);
        }
    }
}
