using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Saga;
using Microsoft.Extensions.Logging;
using System;
using System.Security.Cryptography;
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
        private readonly IMessagingInfrastructureReceiver<TMessage> _infrastructureReceiver;
        private readonly IBrokeredMessageDetailProvider _brokeredMessageDetailProvider;
        private readonly IMessageDestinationRouter<DestinationRouterContext> _nextDestinationRouter;
        private readonly IReplyRouter _replyRouter;
        private readonly ICompensateRouter _compensateRouter;
        private readonly IMessageDispatcher _messageDispatcher;
        private readonly ILogger<BrokeredMessageReceiver<TMessage>> _logger;
        TaskCompletionSource<bool> _completedReceivingSource = new TaskCompletionSource<bool>();
        private CancellationTokenSource _receiverCancellationSource;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="infrastructureReceiver">The message broker infrastructure</param>
        /// <param name="brokeredMessageDetailProvider">Provides routing details to the brokered message receiver</param>
        /// <param name="nextDestinationRouter">Routes brokered messages to the next destination after being received</param>
        /// <param name="replyRouter">Routes brokered messages to the reply destination after being received</param>
        /// <param name="compensateRouter">Routes brokered messages to the compensate destination after being unsuccessfully received</param>
        /// <param name="messageDispatcher">Dispatches messages of <typeparamref name="TMessage"/> to the appropriate <see cref="IMessageHandler{TMessage}"/></param>
        /// <param name="logger">Provides logging capability</param>
        public BrokeredMessageReceiver(IMessagingInfrastructureReceiver<TMessage> infrastructureReceiver,
                                       IBrokeredMessageDetailProvider brokeredMessageDetailProvider,
                                       IMessageDestinationRouter<DestinationRouterContext> nextDestinationRouter,
                                       IReplyRouter replyRouter,
                                       ICompensateRouter compensateRouter,
                                       IMessageDispatcher messageDispatcher,
                                       ILogger<BrokeredMessageReceiver<TMessage>> logger)
        {
            _syncLock = new object();
            _infrastructureReceiver = infrastructureReceiver ?? throw new ArgumentNullException(nameof(infrastructureReceiver));
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
            _nextDestinationRouter = nextDestinationRouter ?? throw new ArgumentNullException(nameof(nextDestinationRouter));
            _replyRouter = replyRouter ?? throw new ArgumentNullException(nameof(replyRouter));
            _compensateRouter = compensateRouter ?? throw new ArgumentNullException(nameof(compensateRouter));
            _messageDispatcher = messageDispatcher ?? throw new ArgumentNullException(nameof(messageDispatcher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (AutoReceiveMessages)
            {
                StartReceiver();
            }
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
                                                 Func<TMessage, IMessageBrokerContext, Task> receiverHandler)
        {
            try
            {
                if (messageContext is null)
                {
                    throw new ArgumentNullException(nameof(messageContext), $"A {typeof(MessageBrokerContext).Name} was not created by the messaging infrastructure.");
                }

                var inboundMessage = messageContext.BrokeredMessage;

                try
                {
                    if (inboundMessage.IsError)
                    {
                        CreateErrorContextFromHeaders(messageContext, inboundMessage);
                        if (!string.IsNullOrWhiteSpace(CompensateDestinationPath))
                        {
                            CreateCompensationContextFromHeaders(messageContext, inboundMessage);
                        }
                    }

                    CreateSagaContextFromHeaders(messageContext);

                    inboundMessage.UpdateVia(Description);

                    if (transactionContext is null)
                    {
                        transactionContext = new TransactionContext(MessageReceiverPath, inboundMessage.TransactionMode);
                    }

                    messageContext.Container.Include(transactionContext);

                    messageContext.NextDestinationRouter = _nextDestinationRouter;
                    messageContext.ReplyRouter = _replyRouter;
                    messageContext.CompensateRouter = _compensateRouter;

                    CreateReplyContextFromHeaders(messageContext, inboundMessage);

                    CreateNextDestinationContextFromHeaders(messageContext);

                    var operationData = inboundMessage.GetMessageFromBody<TMessage>();

                    await receiverHandler(operationData, messageContext).ConfigureAwait(false);
                    await messageContext.NextDestinationRouter.Route(inboundMessage, transactionContext, messageContext.GetNextDestinationContext()).ConfigureAwait(false);
                    await messageContext.ReplyRouter.Route(inboundMessage, transactionContext, messageContext.GetReplyContext()).ConfigureAwait(false);

                    inboundMessage.SuccessfullyReceived = true;
                }
                catch (Exception causeOfCompensation)
                {
                    if (string.IsNullOrWhiteSpace(CompensateDestinationPath))
                    {
                        throw;
                    }

                    try
                    {
                        await messageContext.CompensateRouter.Route(CompensateDestinationPath,
                                                                    inboundMessage,
                                                                    messageContext,
                                                                    transactionContext,
                                                                    $"{causeOfCompensation.Message} -> {causeOfCompensation.StackTrace}",
                                                                    $"'{typeof(TMessage).Name}' was not received successfully").ConfigureAwait(false);
                    }
                    catch (Exception causeOfRoutingFailure)
                    {
                        messageContext.Container.TryGet<CompensateContext>(out var context);
                        throw new CompensationRoutingException(context, causeOfRoutingFailure);
                    }
                }
            }
            catch (CompensationRoutingException cre)
            {
                var errorContext = new ErrorContext(
                   $"Routing compensation message of type '{typeof(TMessage).Name}' to path '{CompensateDestinationPath?.ToString()}' failed",
                   $"Compensation reason: '{cre.CompensateContext?.ToString()}'\n" +
                   $"Routing failure reason: '{cre.InnerException?.Message}'");

                messageContext.SetError(errorContext);

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

                messageContext.SetError(errorContext);

                throw new CriticalBrokeredMessageReceiverException(errorContext, e);
            }
        }

        private static void CreateErrorContextFromHeaders(MessageBrokerContext messageContext, InboundBrokeredMessage inboundMessage)
        {
            inboundMessage.ApplicationProperties.TryGetValue(Headers.FailureDetails, out var reason);
            inboundMessage.ApplicationProperties.TryGetValue(Headers.FailureDescription, out var description);
            var errorContext = new ErrorContext((string)reason, (string)description);
            messageContext.SetError(errorContext);
        }

        private void CreateSagaContextFromHeaders(MessageBrokerContext messageContext)
        {
            if (messageContext.BrokeredMessage.ApplicationProperties.TryGetValue(Headers.SagaId, out var sagaId))
            {
                messageContext.BrokeredMessage.ApplicationProperties.TryGetValue(Headers.SagaStatus, out var sagaStatus);
                var sagaContext = new SagaContext((string)sagaId, MessageReceiverPath, NextDestinationPath, (SagaStatusEnum)sagaStatus, parentContainer: messageContext.Container);
                messageContext.Container.Include(sagaContext);
            }
        }

        private void CreateNextDestinationContextFromHeaders(MessageBrokerContext messageContext)
        {
            if (!string.IsNullOrWhiteSpace(NextDestinationPath))
            {
                var nextDestinationContext = new DestinationRouterContext(NextDestinationPath, null, messageContext.Container);
                messageContext.Container.Include(nextDestinationContext);
            }
        }

        private static void CreateReplyContextFromHeaders(MessageBrokerContext messageContext, InboundBrokeredMessage inboundMessage)
        {
            if (inboundMessage.ApplicationProperties.TryGetValue(Headers.ReplyTo, out var replyTo))
            {
                inboundMessage.ApplicationProperties.TryGetValue(Headers.ReplyToGroupId, out var replyToSessionId);
                inboundMessage.ApplicationProperties.TryGetValue(Headers.GroupId, out var groupId);
                replyToSessionId = !string.IsNullOrWhiteSpace((string)replyToSessionId) ? (string)replyToSessionId : (string)groupId;
                var replyContext = new ReplyDestinationContext((string)replyTo, null, (string)replyToSessionId, messageContext.Container);
                messageContext.Container.Include(replyContext);
            }
        }

        private void CreateCompensationContextFromHeaders(MessageBrokerContext messageContext, InboundBrokeredMessage inboundMessage)
        {
            inboundMessage.ApplicationProperties.TryGetValue(Headers.FailureDetails, out var detail);
            inboundMessage.ApplicationProperties.TryGetValue(Headers.FailureDescription, out var description);
            var compensateContext = new CompensateContext(CompensateDestinationPath, null, (string)detail, (string)description, messageContext.Container);
            messageContext.Container.Include(compensateContext);
        }
    }
}
