using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
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
        private readonly IMessagingInfrastructureReceiver _infrastructureReceiver;
        private readonly ILogger<BrokeredMessageReceiver<TMessage>> _logger;
        private readonly IServiceScopeFactory _serviceFactory;
        private readonly ReceiverOptions _options;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="infrastructureReceiver">The message broker infrastructure</param>
        /// <param name="serviceFactory">The service scope factory used to create a new scope when a message is received from the messaging infrastructure.</param>
        /// <param name="logger">Provides logging capability</param>
        public BrokeredMessageReceiver(ReceiverOptions options,
                                       IMessagingInfrastructureReceiver infrastructureReceiver,
                                       ILogger<BrokeredMessageReceiver<TMessage>> logger,
                                       IServiceScopeFactory serviceFactory,
                                       IBrokeredMessagePathBuilder brokeredMessagePathBuilder)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.MessageReceiverPath = brokeredMessagePathBuilder.GetMessageReceivingPath(options.SendingPath, options.MessageReceiverPath);
            _options.Description = options.Description ?? _options.MessageReceiverPath;
            _infrastructureReceiver = infrastructureReceiver ?? throw new ArgumentNullException(nameof(infrastructureReceiver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        }

        /// <summary>
        /// Indicates if the <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> is currently receiving messages
        /// </summary>
        public bool IsReceiving { get; private set; } = false;

        public Task StartReceiver()
            => Start(CancellationToken.None);

        ///<inheritdoc/>
        public Task StartReceiver(CancellationToken receiverTerminationToken)
            => Start(receiverTerminationToken);

        public Task StopReceiver()
            => _infrastructureReceiver.StopReceiver();

        Task Start(CancellationToken receiverTerminationToken)
        {
            if (receiverTerminationToken == null)
            {
                throw new ArgumentNullException(nameof(receiverTerminationToken), $"A {typeof(CancellationToken).Name} is required in order for the operation to terminate successfully.");
            }

            //using var reg = receiverTerminationToken.Register(async () =>
            //{
            //    _logger.LogTrace($"Stopping {nameof(BrokeredMessageReceiver<TMessage>)}...");
            //    await StopReceiver();
            //    _logger.LogInformation($"{nameof(BrokeredMessageReceiver<TMessage>)} stopped successfully.");
            //});

            var receiveTask = _infrastructureReceiver.StartReceiver(_options,
                                                                    ReceiveInboundBrokeredMessage);

            _logger.LogInformation($"'{GetType().FullName}' has started receiving messages.");

            return receiveTask;
        }

        async Task ReceiveInboundBrokeredMessage([NotNull] MessageBrokerContext messageContext,
                                                 [NotNull] TransactionContext transactionContext)
        {
            try
            {
                TMessage brokeredMessagePayload = null;

                try
                {
                    if (messageContext is null)
                    {
                        throw new ArgumentNullException(nameof(messageContext), $"A {typeof(MessageBrokerContext).Name} was not created by the messaging infrastructure.");
                    }

                    var inboundMessage = messageContext.BrokeredMessage;

                    inboundMessage.UpdateVia(_options.Description);

                    if (transactionContext is null)
                    {
                        transactionContext = new TransactionContext(_options.MessageReceiverPath, _options.TransactionMode.Value);
                    }

                    messageContext.Container.Include(transactionContext);

                    brokeredMessagePayload = inboundMessage.GetMessageFromBody<TMessage>();
                }
                catch (Exception e)
                {
                    throw new PoisonedMessageException($"Unable to build {typeof(MessageBrokerContext).Name} due to poisoned message", e);
                }

                using var scope = _serviceFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();
                messageContext.ExternalDispatcher = scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>();
                await dispatcher.Dispatch(brokeredMessagePayload, messageContext).ConfigureAwait(false);
            }
            catch (PoisonedMessageException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new ReceiverMessageDispatchingException($"Error dispatching message '{typeof(TMessage).Name}' received by '{typeof(BrokeredMessageReceiver<>).Name}'", e);
            }
        }
    }
}
