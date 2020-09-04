using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Saga;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Hosting;
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
    class BrokeredMessageReceiver<TMessage> : BackgroundService, IBrokeredMessageReceiver<TMessage> where TMessage : class, IMessage
    {
        readonly object _syncLock;
        private readonly IMessagingInfrastructureReceiver<TMessage> _infrastructureReceiver;
        private readonly IBrokeredMessageDetailProvider _brokeredMessageDetailProvider;
        private readonly IBrokeredMessageDispatcher _brokeredMessageDispatcher;
        private readonly IMessageDispatcher _messageDispatcher;
        private readonly ILogger<BrokeredMessageReceiver<TMessage>> _logger;
        TaskCompletionSource<bool> _completedReceivingSource = new TaskCompletionSource<bool>();
        private CancellationTokenSource _receiverCancellationSource;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="infrastructureReceiver">The message broker infrastructure</param>
        /// <param name="brokeredMessageDetailProvider">Provides routing details to the brokered message receiver</param>
        /// <param name="messageDispatcher">Dispatches messages of <typeparamref name="TMessage"/> to the appropriate <see cref="IMessageHandler{TMessage}"/></param>
        /// <param name="logger">Provides logging capability</param>
        public BrokeredMessageReceiver(IMessagingInfrastructureReceiver<TMessage> infrastructureReceiver,
                                       IBrokeredMessageDetailProvider brokeredMessageDetailProvider,
                                       IBrokeredMessageDispatcher brokeredMessageDispatcher,
                                       IMessageDispatcher messageDispatcher,
                                       ILogger<BrokeredMessageReceiver<TMessage>> logger)
        {
            _syncLock = new object();
            _infrastructureReceiver = infrastructureReceiver ?? throw new ArgumentNullException(nameof(infrastructureReceiver));
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
            _brokeredMessageDispatcher = brokeredMessageDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageDispatcher));
            _messageDispatcher = messageDispatcher ?? throw new ArgumentNullException(nameof(messageDispatcher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// The receiver should automatically receive brokered messages when the <see cref="BrokeredMessageReceiver{TMessage}"/> is created
        /// </summary>
        public bool AutoReceiveMessages => _brokeredMessageDetailProvider.AutoReceiveMessages<TMessage>();

        /// <summary>
        /// Describes the receiver. Used to track progress using the 'Via' user property of the <see cref="InboundBrokeredMessage"/>./>
        /// </summary>
        public string Description => _brokeredMessageDetailProvider.GetBrokeredMessageDescription<TMessage>();

        /// <summary>
        /// Gets the name of the current destination path.
        /// </summary>
        public string DestinationPath => _brokeredMessageDetailProvider.GetMessageName<TMessage>();

        /// <summary>
        /// Gets the name of the next destination path.
        /// </summary>
        public string NextDestinationPath => _brokeredMessageDetailProvider.GetNextMessageName<TMessage>();

        /// <summary>
        /// Gets the name of the <see cref="DestinationPath"/> for compensation.
        /// </summary>
        public string CompensateDestinationPath => _brokeredMessageDetailProvider.GetCompensatingMessageName<TMessage>();

        /// <summary>
        /// Gets the name of the path to receive messages.
        /// </summary>
        public string MessageReceiverPath => _brokeredMessageDetailProvider.GetReceiverName<TMessage>();

        /// <summary>
        /// Indicates if the <see cref="BrokeredMessageReceiver{TMessage}"/> is currently receiving messages
        /// </summary>
        public bool IsReceiving { get; private set; } = false;

        ///<inheritdoc/>
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (AutoReceiveMessages)
            {
                return Start((message, context) => _messageDispatcher.Dispatch(message, context), CancellationToken.None);
            }
            else
            {
                return Task.CompletedTask;
            }
        }

        ///<inheritdoc/>
        public void StartReceiver()
            => Start((message, context) => _messageDispatcher.Dispatch(message, context), CancellationToken.None);

        ///<inheritdoc/>
        public Task StartReceiver(Func<TMessage, IMessageBrokerContext, Task> receiverHandler, CancellationToken receiverTerminationToken)
            => Start(receiverHandler, receiverTerminationToken);

        ///<inheritdoc/>
        public Task StartReceiver(CancellationToken receiverTerminationToken)
            => Start((message, context) => _messageDispatcher.Dispatch(message, context), receiverTerminationToken);

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

        Task Start(Func<TMessage, IMessageBrokerContext, Task> receiverHandler, CancellationToken receiverTerminationToken)
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

                    _infrastructureReceiver.StartReceiver(receiverHandler,
                                                          ReceiveInboundBrokeredMessage,
                                                          receiverTerminationToken);
                    IsReceiving = true;
                    _logger.LogInformation($"'{GetType().FullName}' has started receiving messages.");
                }
                return _completedReceivingSource.Task;
            }
        }

        async Task ReceiveInboundBrokeredMessage(MessageBrokerContext messageContext,
                                                 TransactionContext transactionContext,
                                                 Func<TMessage, IMessageBrokerContext, Task> brokeredMessagePayloadHandler)
        {
            try
            {
                if (messageContext is null)
                {
                    throw new ArgumentNullException(nameof(messageContext), $"A {typeof(MessageBrokerContext).Name} was not created by the messaging infrastructure.");
                }

                var inboundMessage = messageContext.BrokeredMessage;

                messageContext.ExternalDispatcher = _brokeredMessageDispatcher;

                CreateErrorContextFromHeaders(messageContext, inboundMessage);
                CreateSagaContextFromHeaders(messageContext);
                CreateReplyContextFromHeaders(messageContext, inboundMessage);
                CreateNextDestinationContextFromHeaders(messageContext);
                CreateCompensationContextFromHeaders(messageContext, inboundMessage);

                inboundMessage.WithRouteToSelfPath(this.DestinationPath);
                inboundMessage.UpdateVia(Description);

                if (transactionContext is null)
                {
                    transactionContext = new TransactionContext(MessageReceiverPath, inboundMessage.TransactionMode);
                }

                messageContext.Container.Include(transactionContext);

                var brokeredMessagePayload = inboundMessage.GetMessageFromBody<TMessage>();
                await brokeredMessagePayloadHandler(brokeredMessagePayload, messageContext).ConfigureAwait(false);

                inboundMessage.SuccessfullyReceived = true;
            }
            catch (CompensationRoutingException cre)
            {
                var errorContext = new ErrorContext(
                   $"Routing compensation message of type '{typeof(TMessage).Name}' to '{cre.RoutingContext.DestinationPath?.ToString()}' failed",
                   $"Compensation reason: '{cre.RoutingContext?.ToString()}'\n" +
                   $"Routing failure reason: '{cre.InnerException?.Message}'");

                messageContext.SetFailure(errorContext);

                throw new CriticalBrokeredMessageReceiverException(errorContext, cre);
            }
            catch (Exception e)
            {
                ErrorContext errorContext;

                if (messageContext is null)
                {
                    errorContext = new ErrorContext(
                        $"A brokered message was received with no {typeof(MessageBrokerContext).Name}",
                        $"{e.Message}");

                    throw new CriticalBrokeredMessageReceiverException(errorContext, e);
                }

                errorContext = new ErrorContext(
                    $"An error was encountered receiving message '{typeof(TMessage).Name}'",
                    $"{e.Message}");

                messageContext.SetFailure(errorContext);

                throw new CriticalBrokeredMessageReceiverException(errorContext, e);
            }
        }

        private static void CreateErrorContextFromHeaders(MessageBrokerContext messageContext, InboundBrokeredMessage inboundMessage)
        {
            if (inboundMessage.IsError)
            {
                inboundMessage.ApplicationProperties.TryGetValue(ApplicationProperties.FailureDetails, out var reason);
                inboundMessage.ApplicationProperties.TryGetValue(ApplicationProperties.FailureDescription, out var description);
                var errorContext = new ErrorContext((string)reason, (string)description);
                messageContext.SetFailure(errorContext);
            }
        }

        private void CreateSagaContextFromHeaders(MessageBrokerContext messageContext)
        {
            if (messageContext.BrokeredMessage.ApplicationProperties.TryGetValue(ApplicationProperties.SagaId, out var sagaId))
            {
                messageContext.BrokeredMessage.ApplicationProperties.TryGetValue(ApplicationProperties.SagaStatus, out var sagaStatus);
                var sagaContext = new SagaContext((string)sagaId, MessageReceiverPath, NextDestinationPath, (SagaStatusEnum)sagaStatus, parentContainer: messageContext.Container);
                messageContext.Container.Include(sagaContext);
            }
        }

        private void CreateNextDestinationContextFromHeaders(MessageBrokerContext messageContext)
        {
            if (!string.IsNullOrWhiteSpace(NextDestinationPath))
            {
                var nextDestinationContext = new RoutingContext(NextDestinationPath, messageContext.Container);
                messageContext.Container.Include(nextDestinationContext);
            }
        }

        private static void CreateReplyContextFromHeaders(MessageBrokerContext messageContext, InboundBrokeredMessage inboundMessage)
        {
            if (inboundMessage.ApplicationProperties.TryGetValue(ApplicationProperties.ReplyToAddress, out var replyTo))
            {
                inboundMessage.ApplicationProperties.TryGetValue(ApplicationProperties.ReplyToGroupId, out var replyToSessionId);
                inboundMessage.ApplicationProperties.TryGetValue(ApplicationProperties.GroupId, out var groupId);
                replyToSessionId = !string.IsNullOrWhiteSpace((string)replyToSessionId) ? (string)replyToSessionId : (string)groupId;
                var replyContext = new ReplyToRoutingContext((string)replyTo, (string)replyToSessionId, messageContext.Container);
                messageContext.Container.Include(replyContext);
                inboundMessage.ClearReplyToProperties();
            }
        }

        private void CreateCompensationContextFromHeaders(MessageBrokerContext messageContext, InboundBrokeredMessage inboundMessage)
        {
            if (!string.IsNullOrWhiteSpace(CompensateDestinationPath))
            {
                inboundMessage.ApplicationProperties.TryGetValue(ApplicationProperties.FailureDetails, out var detail);
                inboundMessage.ApplicationProperties.TryGetValue(ApplicationProperties.FailureDescription, out var description);
                var compensateContext = new CompensationRoutingContext(CompensateDestinationPath, (string)detail, (string)description, messageContext.Container);
                messageContext.Container.Include(compensateContext);
            }
        }
    }
}
