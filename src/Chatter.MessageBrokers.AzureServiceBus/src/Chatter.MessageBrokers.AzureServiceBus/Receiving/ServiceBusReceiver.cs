using Chatter.CQRS;
using Chatter.MessageBrokers.AzureServiceBus.Extensions;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Receiving;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    public class ServiceBusReceiver<TMessage> : IMessagingInfrastructureReceiver<TMessage> where TMessage : class, IMessage
    {
        readonly object _syncLock;
        private readonly IBrokeredMessageDetailProvider _brokeredMessageDetailProvider;
        private readonly ILogger<ServiceBusReceiver<TMessage>> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        MessageReceiver _innerReceiver;

        public ServiceBusReceiver(ServiceBusOptions serviceBusConfiguration,
                                  IBrokeredMessageDetailProvider brokeredMessageDetailProvider,
                                  ILogger<ServiceBusReceiver<TMessage>> logger,
                                  IBodyConverterFactory bodyConverterFactory)
        {
            if (serviceBusConfiguration is null)
            {
                throw new ArgumentNullException(nameof(serviceBusConfiguration));
            }

            _syncLock = new object();
            ServiceBusConnection = new ServiceBusConnection(serviceBusConfiguration.ConnectionString, serviceBusConfiguration.Policy);
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
        }

        /// <summary>
        /// Connection object to the service bus namespace.
        /// </summary>
        public ServiceBusConnection ServiceBusConnection { get; }

        public bool AutoReceiveMessages => _brokeredMessageDetailProvider.AutoReceiveMessages<TMessage>();

        /// <summary>
        /// Describes the receiver pipeline. Used to track progress using the 'Via' user property of the <see cref="Message"/>./>
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
        /// Gets the name of the <see cref="DestinationPath"/> of the compensating path.
        /// </summary>
        public string CompensatingEntityPath => _brokeredMessageDetailProvider.GetCompensatingMessageName<TMessage>();

        /// <summary>
        /// Gets the name of the path to receive messages.
        /// </summary>
        public string MessageReceiverPath => _brokeredMessageDetailProvider.GetReceiverName<TMessage>();

        internal MessageReceiver InnerReceiver
        {
            get
            {
                if (_innerReceiver == null)
                {
                    lock (_syncLock)
                    {
                        if (_innerReceiver == null)
                        {
                            _innerReceiver = new MessageReceiver(this.ServiceBusConnection,
                                                                 this.MessageReceiverPath);
                        }
                    }
                }

                return _innerReceiver;
            }
        }

        public void StartReceiver(Func<TMessage, IMessageBrokerContext, Task> receiverHandler,
                                  Func<MessageBrokerContext, TransactionContext, Func<TMessage, IMessageBrokerContext, Task>, Task> brokeredMessageHandler,
                                  CancellationToken receiverTerminationToken)
        {
            receiverTerminationToken.Register(
            () =>
            {
                this.InnerReceiver.CloseAsync();
                _innerReceiver = null;
            });

            var messageHandlerOptions = new MessageHandlerOptions(HandleMessageException)
            {
                AutoComplete = false,
                MaxConcurrentCalls = 1 //TODO: config
            };

            this.InnerReceiver.RegisterMessageHandler(async (msg, receivePumpToken) =>
            {
                var transactionMode = msg.GetTransactionMode();

                using var scope = CreateTransactionScope(transactionMode);
                try
                {
                    msg.AddUserProperty(Headers.TimeToLive, msg.TimeToLive);
                    msg.AddUserProperty(Headers.ExpiryTimeUtc, msg.ExpiresAtUtc);

                    var bodyConverter = _bodyConverterFactory.CreateBodyConverter(msg.ContentType);

                    var messageContext = new MessageBrokerContext(msg.MessageId, msg.Body, msg.UserProperties, this.MessageReceiverPath, bodyConverter);
                    messageContext.Container.Set(msg);

                    var transactionContext = new TransactionContext(this.MessageReceiverPath, transactionMode);
                    transactionContext.Container.Set(this.InnerReceiver);

                    if (transactionMode == TransactionMode.FullAtomicity)
                    {
                        transactionContext.Container.Set(this.ServiceBusConnection);
                    }

                    await brokeredMessageHandler(messageContext, transactionContext, receiverHandler).ConfigureAwait(false);

                    await this.InnerReceiver.CompleteAsync(msg.SystemProperties.LockToken).ConfigureAwait(false);
                }
                catch (CriticalBrokeredMessageReceiverException cbmre)
                {
                    await AbandonWithExponentialDelayAsync(msg).ConfigureAwait(false);
                    _logger.LogError($"Critical error encountered receiving message. MessageId: '{msg.MessageId}, CorrelationId: '{msg.CorrelationId}'"
                                   + "\n"
                                   + $"{cbmre.ErrorContext}");
                }
                catch (Exception e)
                {
                    await AbandonWithExponentialDelayAsync(msg).ConfigureAwait(false);
                    _logger.LogWarning($"Message with Id '{msg.MessageId}' has been abandoned:\n '{e}'");
                }
                finally
                {
                    scope?.Complete();
                }
            }, messageHandlerOptions);
        }

        private async Task AbandonWithExponentialDelayAsync(Message msg)
        {
            var secondsOfDelay = BrokeredMessageReceiverRetry.ExponentialDelay(msg.SystemProperties.DeliveryCount);
            await Task.Delay(secondsOfDelay * BrokeredMessageReceiverRetry.MillisecondsInASecond);
            await this.InnerReceiver.AbandonAsync(msg.SystemProperties.LockToken, msg.UserProperties).ConfigureAwait(false);
        }

        TransactionScope CreateTransactionScope(TransactionMode transactionMode)
        {
            if (transactionMode == TransactionMode.None)
            {
                return null;
            }
            else
            {
                return new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            }
        }

        protected Task HandleMessageException(ExceptionReceivedEventArgs e)
        {
            _logger?.LogError($"Error receiving message for '{GetType().Name}': {e.Exception.Message}, Action: {e.ExceptionReceivedContext.Action}, Entity: {e.ExceptionReceivedContext.EntityPath}");
            return Task.CompletedTask;
        }
    }
}
