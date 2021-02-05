using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using System;
using System.Collections.Generic;
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
            => Dispatch(new[] { message }, transactionContext, options ?? new SendOptions(), destinationPath);

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, string destinationPath, IMessageHandlerContext messageHandlerContext, SendOptions options = null) where TMessage : ICommand
           => Send(message, destinationPath, messageHandlerContext?.GetTransactionContext(), MergeSendOptionsWithMessageContext(messageHandlerContext, options));

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand
            => Dispatch(new[] { message }, transactionContext, options ?? new SendOptions());

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, SendOptions options = null) where TMessage : ICommand
            => Send(message, messageHandlerContext?.GetTransactionContext(), MergeSendOptionsWithMessageContext(messageHandlerContext, options));

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
            => Dispatch(new[] { message }, transactionContext, options ?? new PublishOptions(), destinationPath);

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, string destinationPath, IMessageHandlerContext messageHandlerContext, PublishOptions options = null) where TMessage : IEvent
            => Publish(message, destinationPath, messageHandlerContext?.GetTransactionContext(), MergePublishOptionsWithMessageContext(messageHandlerContext, options));

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
            => Dispatch(new[] { message }, transactionContext, options ?? new PublishOptions());

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, PublishOptions options = null) where TMessage : IEvent
            => Publish(message, messageHandlerContext?.GetTransactionContext(), MergePublishOptionsWithMessageContext(messageHandlerContext, options));

        /// <inheritdoc/>
        public Task Publish<TMessage>(IEnumerable<TMessage> messages, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
            => Dispatch(messages, transactionContext, options ?? new PublishOptions());

        /// <inheritdoc/>
        public Task Publish<TMessage>(IEnumerable<TMessage> messages, IMessageHandlerContext messageHandlerContext, PublishOptions options = null) where TMessage : IEvent
            => Publish(messages, messageHandlerContext?.GetTransactionContext(), MergePublishOptionsWithMessageContext(messageHandlerContext, options));

        public Task Forward(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext)
            => _forwarder.Route(inboundBrokeredMessage, forwardDestination, transactionContext);

        public Task Forward(string forwardDestination, IMessageBrokerContext context)
            => Forward(context.BrokeredMessage, forwardDestination, context?.GetTransactionContext());

        Task Dispatch<TMessage, TOptions>(IEnumerable<TMessage> messages, TransactionContext transactionContext, TOptions options, string destinationPath = null)
        where TMessage : IMessage
        where TOptions : RoutingOptions, new()
        {
            var outbounds = Dispatch(messages, destinationPath, options);
            options.MessageContext.TryGetValue(MessageContext.InfrastructureType, out var infraType);
            return _messageRouter.Route(outbounds, transactionContext, (string)infraType);
        }

        IEnumerable<OutboundBrokeredMessage> Dispatch<TMessage, TOptions>(IEnumerable<TMessage> messages, string destinationPath, TOptions options)
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

            foreach (var message in messages)
            {
                var destination = string.IsNullOrWhiteSpace(destinationPath)
                    ? _brokeredMessageDetailProvider.GetMessageName(message.GetType())
                    : destinationPath;

                if (string.IsNullOrWhiteSpace(destination))
                {
                    throw new ArgumentNullException(nameof(destination), $"Routing destination is required. Use {typeof(BrokeredMessageAttribute).Name} or overload that accepts 'destinationPath'");
                }

                OutboundBrokeredMessage outbound;

                if (string.IsNullOrWhiteSpace(options.MessageId))
                {
                    outbound = new OutboundBrokeredMessage(_messageIdGenerator, message, options.MessageContext, destination, converter);
                }
                else
                {
                    outbound = new OutboundBrokeredMessage(options.MessageId, message, options.MessageContext, destination, converter);
                }

                yield return outbound;
            }
        }

        private SendOptions MergeSendOptionsWithMessageContext(IMessageHandlerContext messageHandlerContext, SendOptions options)
            => SendOptions.Create(messageHandlerContext?.GetInboundBrokeredMessage()?.MessageContextImpl).Merge(options);

        private PublishOptions MergePublishOptionsWithMessageContext(IMessageHandlerContext messageHandlerContext, PublishOptions options)
            => PublishOptions.Create(messageHandlerContext?.GetInboundBrokeredMessage()?.MessageContextImpl).Merge(options);
    }
}
