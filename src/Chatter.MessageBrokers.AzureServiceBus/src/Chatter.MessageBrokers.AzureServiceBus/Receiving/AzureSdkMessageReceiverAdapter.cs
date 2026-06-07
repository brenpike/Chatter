using Chatter.MessageBrokers.Exceptions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Azure.ServiceBus.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Production <see cref="IServiceBusMessageReceiver"/> adapter wrapping the Azure Service Bus SDK
    /// <see cref="MessageReceiver"/>. Holds the VERBATIM double-checked-lock lazy construction moved
    /// out of <see cref="ServiceBusReceiver"/>: the SDK receiver is created on first access (opening a
    /// live connection), and is reconstructed after a reset (e.g. following an
    /// <see cref="ObjectDisposedException"/> on a closing receiver).
    /// </summary>
    internal class AzureSdkMessageReceiverAdapter : IServiceBusMessageReceiver
    {
        readonly object _syncLock = new object();
        private readonly ServiceBusConnectionStringBuilder _connectionStringBuilder;
        private readonly string _messageReceiverPath;
        private readonly ReceiveMode _receiveMode;
        private readonly RetryPolicy _retryPolicy;
        private readonly int _prefetchCount;
        private readonly ITokenProvider _tokenProvider;
        private readonly ILogger _logger;
        private MessageReceiver _innerReceiver;

        public AzureSdkMessageReceiverAdapter(ServiceBusConnectionStringBuilder connectionStringBuilder,
                                              string messageReceiverPath,
                                              ReceiveMode receiveMode,
                                              RetryPolicy retryPolicy,
                                              int prefetchCount,
                                              ITokenProvider tokenProvider,
                                              ILogger logger)
        {
            _connectionStringBuilder = connectionStringBuilder ?? throw new ArgumentNullException(nameof(connectionStringBuilder));
            _messageReceiverPath = messageReceiverPath;
            _receiveMode = receiveMode;
            _retryPolicy = retryPolicy;
            _prefetchCount = prefetchCount;
            _tokenProvider = tokenProvider;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        MessageReceiver InnerReceiver
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
                                if (_tokenProvider is NullTokenProvider)
                                {
                                    _innerReceiver = new MessageReceiver(_connectionStringBuilder.GetNamespaceConnectionString(),
                                                                         _messageReceiverPath,
                                                                         _receiveMode,
                                                                         _retryPolicy,
                                                                         _prefetchCount);
                                    _logger.LogTrace($"{nameof(MessageReceiver)} created for '{_messageReceiverPath}' on endpoint '{_connectionStringBuilder.Endpoint}'");
                                }
                                else
                                {
                                    _innerReceiver = new MessageReceiver(_connectionStringBuilder.Endpoint,
                                                                         _messageReceiverPath,
                                                                         _tokenProvider,
                                                                         _connectionStringBuilder.TransportType,
                                                                         _receiveMode,
                                                                         _retryPolicy,
                                                                         _prefetchCount);
                                    _logger.LogTrace($"{nameof(MessageReceiver)} created for '{_messageReceiverPath}' on endpoint '{_connectionStringBuilder.Endpoint}' using {_tokenProvider.GetType().Name}");
                                }
                            }
                            catch (ArgumentException e) //throw when service bus connection string cannot be built
                            {
                                throw new CriticalReceiverException($"Error creating {nameof(MessageReceiver)}", e);
                            }
                        }
                    }
                }

                return _innerReceiver;
            }
        }

        public ServiceBusConnection ServiceBusConnection => InnerReceiver.ServiceBusConnection;

        public bool IsClosedOrClosing => _innerReceiver != null && _innerReceiver.IsClosedOrClosing;

        public Task<Message> ReceiveAsync() => InnerReceiver.ReceiveAsync();

        public Task CompleteAsync(string lockToken) => InnerReceiver.CompleteAsync(lockToken);

        public Task AbandonAsync(string lockToken, IDictionary<string, object> propertiesToModify)
            => InnerReceiver.AbandonAsync(lockToken, propertiesToModify);

        public Task DeadLetterAsync(string lockToken, string deadLetterReason, string deadLetterErrorDescription)
            => InnerReceiver.DeadLetterAsync(lockToken, deadLetterReason, deadLetterErrorDescription);

        public async Task CloseAsync()
        {
            MessageReceiver toClose;
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
