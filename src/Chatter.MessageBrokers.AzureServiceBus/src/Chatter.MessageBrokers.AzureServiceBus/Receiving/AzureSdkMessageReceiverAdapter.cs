using Chatter.MessageBrokers.Exceptions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SdkServiceBusReceiver = Azure.Messaging.ServiceBus.ServiceBusReceiver;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Production <see cref="IServiceBusMessageReceiver"/> adapter wrapping the Azure.Messaging.ServiceBus
    /// SDK <see cref="SdkServiceBusReceiver"/>. The SDK receiver is long-lived and created on first access
    /// from a shared <see cref="ServiceBusClient"/> (the client opens the live connection), and is
    /// reconstructed after a reset (e.g. following an <see cref="ObjectDisposedException"/> on a closed
    /// receiver) by recreating it from that same shared client.
    /// </summary>
    internal class AzureSdkMessageReceiverAdapter : IServiceBusMessageReceiver
    {
        readonly object _syncLock = new object();
        private readonly ServiceBusClient _client;
        private readonly string _messageReceiverPath;
        private readonly ServiceBusReceiveMode _receiveMode;
        private readonly int _prefetchCount;
        private readonly ILogger _logger;
        private SdkServiceBusReceiver _innerReceiver;

        public AzureSdkMessageReceiverAdapter(ServiceBusClient client,
                                              string messageReceiverPath,
                                              ServiceBusReceiveMode receiveMode,
                                              int prefetchCount,
                                              ILogger logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _messageReceiverPath = messageReceiverPath;
            _receiveMode = receiveMode;
            _prefetchCount = prefetchCount;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        SdkServiceBusReceiver InnerReceiver
        {
            get
            {
                if (_innerReceiver == null)
                {
                    lock (_syncLock)
                    {
                        if (_innerReceiver == null)
                        {
                            try
                            {
                                _innerReceiver = _client.CreateReceiver(_messageReceiverPath, new ServiceBusReceiverOptions
                                {
                                    ReceiveMode = _receiveMode,
                                    PrefetchCount = _prefetchCount,
                                });
                                _logger.LogTrace($"{nameof(SdkServiceBusReceiver)} created for '{_messageReceiverPath}' on endpoint '{_client.FullyQualifiedNamespace}'");
                            }
                            catch (ArgumentException e) //throw when the receiver cannot be created (e.g. invalid entity path)
                            {
                                throw new CriticalReceiverException($"Error creating {nameof(SdkServiceBusReceiver)}", e);
                            }
                        }
                    }
                }

                return _innerReceiver;
            }
        }

        public bool IsClosedOrClosing => _innerReceiver != null && _innerReceiver.IsClosed;

        public Task<ServiceBusReceivedMessage> ReceiveAsync() => InnerReceiver.ReceiveMessageAsync();

        public Task CompleteAsync(ServiceBusReceivedMessage message) => InnerReceiver.CompleteMessageAsync(message);

        public Task AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
            => InnerReceiver.AbandonMessageAsync(message, propertiesToModify);

        public Task DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
            => InnerReceiver.DeadLetterMessageAsync(message, deadLetterReason, deadLetterErrorDescription);

        public async Task CloseAsync()
        {
            SdkServiceBusReceiver toClose;
            lock (_syncLock)
            {
                toClose = _innerReceiver;
                _innerReceiver = null;
            }

            if (toClose != null)
            {
                await toClose.CloseAsync();
            }
        }
    }
}
