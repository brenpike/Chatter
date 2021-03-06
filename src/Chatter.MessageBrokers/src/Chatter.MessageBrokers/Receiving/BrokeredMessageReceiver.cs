﻿using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
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
    public class BrokeredMessageReceiver<TMessage> : IBrokeredMessageReceiver<TMessage> where TMessage : class, IMessage
    {
        private IMessagingInfrastructureReceiver _infrastructureReceiver;
        private readonly IMessagingInfrastructureProvider _infrastructureProvider;
        protected readonly ILogger<BrokeredMessageReceiver<TMessage>> _logger;
        protected readonly IServiceScopeFactory _serviceFactory;
        protected ReceiverOptions _options;
        private bool _disposedValue;

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="infrastructureProvider">The message broker infrastructure</param>
        /// <param name="serviceFactory">The service scope factory used to create a new scope when a message is received from the messaging infrastructure.</param>
        /// <param name="logger">Provides logging capability</param>
        public BrokeredMessageReceiver(IMessagingInfrastructureProvider infrastructureProvider,
                                       ILogger<BrokeredMessageReceiver<TMessage>> logger,
                                       IServiceScopeFactory serviceFactory)
        {
            if (infrastructureProvider is null)
            {
                throw new ArgumentNullException(nameof(infrastructureProvider));
            }

            _infrastructureProvider = infrastructureProvider;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        }

        /// <summary>
        /// Indicates if the <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> is currently receiving messages
        /// </summary>
        public bool IsReceiving { get; private set; } = false;

        public Task<IAsyncDisposable> StartReceiver(ReceiverOptions options)
            => StartReceiver(options, CancellationToken.None);

        ///<inheritdoc/>
        public async Task<IAsyncDisposable> StartReceiver(ReceiverOptions options, CancellationToken receiverTerminationToken)
        {
            await Start(options, receiverTerminationToken).ConfigureAwait(false);
            return this;
        }

        public Task StopReceiver()
            => _infrastructureReceiver.StopReceiver();

        Task Start(ReceiverOptions options, CancellationToken receiverTerminationToken)
        {
            if (receiverTerminationToken == null)
            {
                throw new ArgumentNullException(nameof(receiverTerminationToken), $"A {typeof(CancellationToken).Name} is required in order for the operation to terminate successfully.");
            }

            options.Description ??= options.MessageReceiverPath;
            _infrastructureReceiver = _infrastructureProvider.GetReceiver(options.InfrastructureType);
            options.MessageReceiverPath = _infrastructureProvider.GetInfrastructure(options.InfrastructureType).PathBuilder.GetMessageReceivingPath(options.SendingPath, options.MessageReceiverPath);

            _options = options;

            var receiveTask = _infrastructureReceiver.StartReceiver(options,
                                                                    ReceiveInboundBrokeredMessage);

            _logger.LogInformation($"'{GetType().FullName}' has started receiving messages.");

            return receiveTask;
        }

        public virtual async Task ReceiveInboundBrokeredMessage(MessageBrokerContext messageContext,
                                                                TransactionContext transactionContext)
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
                messageContext.Container.Include((IExternalDispatcher)scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>());
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

        public async ValueTask DisposeAsync()
        {
            await _infrastructureReceiver.DisposeAsync().ConfigureAwait(false);

            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _infrastructureReceiver?.Dispose();
                }

                _infrastructureReceiver = null;
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
