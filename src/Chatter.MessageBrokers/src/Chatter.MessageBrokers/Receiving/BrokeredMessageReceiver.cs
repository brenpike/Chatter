using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// An infrastructure agnostic receiver of brokered messages of type <typeparamref name="TMessage"/>
    /// </summary>
    /// <typeparam name="TMessage">The type of messages the brokered message receiver accepts</typeparam>
    class BrokeredMessageReceiver<TMessage> : IBrokeredMessageReceiver<TMessage> where TMessage : class, IMessage
    {
        readonly object _syncLock;
        private readonly IMessagingInfrastructureReceiver _infrastructureReceiver;
        private readonly ILogger<BrokeredMessageReceiver<TMessage>> _logger;
        private readonly IServiceScopeFactory _serviceFactory;
        TaskCompletionSource<bool> _completedReceivingSource = new TaskCompletionSource<bool>();
        private CancellationTokenSource _receiverCancellationSource;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="infrastructureReceiver">The message broker infrastructure</param>
        /// <param name="serviceFactory">THe service scope factory used to create a new scope when a message is received from the messaging infrastructure.</param>
        /// <param name="logger">Provides logging capability</param>
        public BrokeredMessageReceiver(string receiverPath,
                                       string description,
                                       IMessagingInfrastructureReceiver infrastructureReceiver,
                                       ILogger<BrokeredMessageReceiver<TMessage>> logger,
                                       IServiceScopeFactory serviceFactory)
        {
            _syncLock = new object();
            MessageReceiverPath = receiverPath;
            Description = description;
            _infrastructureReceiver = infrastructureReceiver ?? throw new ArgumentNullException(nameof(infrastructureReceiver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        }

        /// <summary>
        /// Describes the receiver. Used to track progress using the 'Via' user property of the <see cref="InboundBrokeredMessage"/>./>
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the name of the path to receive messages.
        /// </summary>
        public string MessageReceiverPath { get; }

        /// <summary>
        /// Indicates if the <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> is currently receiving messages
        /// </summary>
        public bool IsReceiving { get; private set; } = false;

        ///<inheritdoc/>
        public void StartReceiver()
            => Start(CancellationToken.None);

        ///<inheritdoc/>
        public Task StartReceiver(CancellationToken receiverTerminationToken)
            => Start(receiverTerminationToken);

        ///<inheritdoc/>
        public void StopReceiver()
        {
            try
            {
                _receiverCancellationSource?.Cancel();
            }
            finally
            {
                _receiverCancellationSource?.Dispose();
            }
        }

        Task Start(CancellationToken receiverTerminationToken)
        {
            lock (_syncLock)
            {
                if (!IsReceiving)
                {
                    _completedReceivingSource = new TaskCompletionSource<bool>();

                    if (receiverTerminationToken == CancellationToken.None)
                    {
                        _receiverCancellationSource = new CancellationTokenSource();
                        receiverTerminationToken = _receiverCancellationSource.Token;
                    }

                    if (receiverTerminationToken == null)
                    {
                        throw new ArgumentNullException(nameof(receiverTerminationToken), $"A {typeof(CancellationToken).Name} is required in order for the operation to terminate successfully.");
                    }

                    receiverTerminationToken.Register(
                    () =>
                    {
                        _completedReceivingSource.SetResult(true);
                        IsReceiving = false;
                    });

                    _infrastructureReceiver.StartReceiver(this.MessageReceiverPath,
                                                          ReceiveInboundBrokeredMessage,
                                                          receiverTerminationToken);
                    IsReceiving = true;
                    _logger.LogInformation($"'{GetType().FullName}' has started receiving messages.");
                }
                return _completedReceivingSource.Task;
            }
        }

        async Task ReceiveInboundBrokeredMessage(MessageBrokerContext messageContext,
                                                 TransactionContext transactionContext)
        {
            try
            {
                if (messageContext is null)
                {
                    throw new ArgumentNullException(nameof(messageContext), $"A {typeof(MessageBrokerContext).Name} was not created by the messaging infrastructure.");
                }

                var inboundMessage = messageContext.BrokeredMessage;

                CreateErrorContextFromHeaders(messageContext, inboundMessage);
                CreateReplyContextFromHeaders(messageContext, inboundMessage);

                inboundMessage.UpdateVia(Description);

                if (transactionContext is null)
                {
                    transactionContext = new TransactionContext(MessageReceiverPath, inboundMessage.TransactionMode);
                }

                messageContext.Container.Include(transactionContext);

                var brokeredMessagePayload = inboundMessage.GetMessageFromBody<TMessage>();

                using var scope = _serviceFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();
                messageContext.BrokeredMessageDispatcher = scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>();
                await dispatcher.Dispatch(brokeredMessagePayload, messageContext).ConfigureAwait(false);

                inboundMessage.SuccessfullyReceived = true;
            }
            catch (Exception e)
            {
                FailureContext failureContext;

                if (messageContext is null)
                {
                    failureContext = new FailureContext(
                        $"A brokered message was received with no {typeof(MessageBrokerContext).Name}",
                        $"{e.Message}");

                    throw new CriticalBrokeredMessageReceiverException(failureContext, e);
                }

                failureContext = new FailureContext(
                    $"An error was encountered receiving message '{typeof(TMessage).Name}'",
                    $"{e.Message}");

                messageContext.SetFailure(failureContext);

                throw new CriticalBrokeredMessageReceiverException(failureContext, e);
            }
        }

        private static void CreateErrorContextFromHeaders(MessageBrokerContext messageContext, InboundBrokeredMessage inboundMessage)
        {
            if (inboundMessage.IsError)
            {
                inboundMessage.MessageContext.TryGetValue(MessageContext.FailureDetails, out var reason);
                inboundMessage.MessageContext.TryGetValue(MessageContext.FailureDescription, out var description);
                var errorContext = new FailureContext((string)reason, (string)description);
                messageContext.SetFailure(errorContext);
            }
        }

        private static void CreateReplyContextFromHeaders(MessageBrokerContext messageContext, InboundBrokeredMessage inboundMessage)
        {
            if (inboundMessage.MessageContext.TryGetValue(MessageContext.ReplyToAddress, out var replyTo))
            {
                inboundMessage.MessageContext.TryGetValue(MessageContext.ReplyToGroupId, out var replyToSessionId);
                inboundMessage.MessageContext.TryGetValue(MessageContext.GroupId, out var groupId);
                replyToSessionId = !string.IsNullOrWhiteSpace((string)replyToSessionId) ? (string)replyToSessionId : (string)groupId;
                var replyContext = new ReplyToRoutingContext((string)replyTo, (string)replyToSessionId, messageContext.Container);
                messageContext.Container.Include(replyContext);
                inboundMessage.ClearReplyToProperties();
            }
        }
    }
}
