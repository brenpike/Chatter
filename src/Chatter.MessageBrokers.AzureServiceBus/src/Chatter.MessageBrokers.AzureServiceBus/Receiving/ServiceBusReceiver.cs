using Chatter.MessageBrokers.AzureServiceBus.Extensions;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
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
    public class ServiceBusReceiver : IMessagingInfrastructureReceiver
    {
        readonly object _syncLock;
        private readonly ILogger<ServiceBusReceiver> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly int _maxConcurrentCalls = 1;
        MessageReceiver _innerReceiver;

        public ServiceBusReceiver(ServiceBusOptions serviceBusConfiguration,
                                  ILogger<ServiceBusReceiver> logger,
                                  IBodyConverterFactory bodyConverterFactory)
        {
            if (serviceBusConfiguration is null)
            {
                throw new ArgumentNullException(nameof(serviceBusConfiguration));
            }

            _syncLock = new object();
            ServiceBusConnection = new ServiceBusConnection(serviceBusConfiguration.ConnectionString, serviceBusConfiguration.Policy);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _maxConcurrentCalls = serviceBusConfiguration.MaxConcurrentCalls;
        }

        /// <summary>
        /// Connection object to the service bus namespace.
        /// </summary>
        public ServiceBusConnection ServiceBusConnection { get; }

        public string MessageReceiverPath { get; private set; }

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

        public void StartReceiver(string receiverPath,
                                  Func<MessageBrokerContext, TransactionContext, Task> brokeredMessageHandler,
                                  CancellationToken receiverTerminationToken)
        {
            this.MessageReceiverPath = receiverPath;

            receiverTerminationToken.Register(
            () =>
            {
                this.InnerReceiver.CloseAsync();
                _innerReceiver = null;
            });

            var messageHandlerOptions = new MessageHandlerOptions(HandleMessageException)
            {
                AutoComplete = false,
                MaxConcurrentCalls = _maxConcurrentCalls
            };

            this.InnerReceiver.RegisterMessageHandler(async (msg, receivePumpToken) =>
            {
                var transactionMode = msg.GetTransactionMode();

                using var scope = CreateTransactionScope(transactionMode);
                try
                {
                    msg.AddUserProperty(MessageContext.TimeToLive, msg.TimeToLive);
                    msg.AddUserProperty(MessageContext.ExpiryTimeUtc, msg.ExpiresAtUtc);

                    var bodyConverter = _bodyConverterFactory.CreateBodyConverter(msg.ContentType);

                    var messageContext = new MessageBrokerContext(msg.MessageId, msg.Body, msg.UserProperties, this.MessageReceiverPath, bodyConverter);
                    messageContext.Container.Include(msg);

                    var transactionContext = new TransactionContext(this.MessageReceiverPath, transactionMode);
                    transactionContext.Container.Include(this.InnerReceiver);

                    if (transactionMode == TransactionMode.FullAtomicityViaInfrastructure)
                    {
                        transactionContext.Container.Include(this.ServiceBusConnection);
                    }

                    await brokeredMessageHandler(messageContext, transactionContext).ConfigureAwait(false);

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
            if (transactionMode == TransactionMode.None || transactionMode == TransactionMode.ReceiveOnly)
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
