using Chatter.MessageBrokers.Exceptions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
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
    /// <remarks>
    /// INVARIANT: the renewal of a PeekLock delivery's message lock is ended BEFORE the settlement that ends the
    /// delivery reaches the broker, and every settle path funnels through the ONE method that does it — mirroring
    /// the session adapter's invariant that the renewal CTS is cancelled before the held receiver is closed on
    /// every release path. Renewal state is PER DELIVERY because one non-session adapter serves
    /// <c>MaxConcurrentCalls</c> in-flight messages at once.
    /// </remarks>
    internal class AzureSdkMessageReceiverAdapter : IServiceBusMessageReceiver
    {
        readonly object _syncLock = new object();
        private readonly ServiceBusClient _client;
        private readonly string _messageReceiverPath;
        private readonly ServiceBusReceiveMode _receiveMode;
        private readonly int _prefetchCount;
        private readonly MessageLockRenewalRegistry _renewals;
        private readonly ILogger _logger;
        private SdkServiceBusReceiver _innerReceiver;

        public AzureSdkMessageReceiverAdapter(ServiceBusClient client,
                                              string messageReceiverPath,
                                              ServiceBusReceiveMode receiveMode,
                                              int prefetchCount,
                                              TimeSpan maxMessageLockRenewalDuration,
                                              ILogger logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _messageReceiverPath = messageReceiverPath;
            _receiveMode = receiveMode;
            _prefetchCount = prefetchCount;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _renewals = new MessageLockRenewalRegistry(maxMessageLockRenewalDuration, messageReceiverPath, _logger);
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

        /// <summary>The number of in-flight deliveries whose message locks this adapter is currently renewing.</summary>
        internal int ActiveRenewalCount => _renewals.ActiveRenewalCount;

        public async Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            // INVARIANT: the SDK receiver that delivers the message is captured HERE and closed over by the renew
            // delegate, never re-read inside it. After an inner-receiver swap a REBUILT receiver must never be
            // handed a message it never delivered.
            var sdkReceiver = InnerReceiver;
            var message = await sdkReceiver.ReceiveMessageAsync(maxWaitTime: null, cancellationToken).ConfigureAwait(false);

            // ReceiveAndDelete removes the delivery as it is received, so there is no lock to renew — the same
            // reason no settlement is owed for it.
            if (message != null && _receiveMode == ServiceBusReceiveMode.PeekLock)
            {
                _renewals.Start(message, renewalToken => sdkReceiver.RenewMessageLockAsync(message, renewalToken));
            }

            return message;
        }

        // Every settle here reaches the SDK receiver directly: a non-session receiver settles against the
        // long-lived receiver that delivered the message, so there is no released-session absence to report.
        // A settlement the broker refuses THROWS rather than answering an outcome.
        public Task<ServiceBusSettlementOutcome> CompleteAsync(ServiceBusReceivedMessage message)
            => SettleAsync(message, sdkReceiver => sdkReceiver.CompleteMessageAsync(message));

        public Task<ServiceBusSettlementOutcome> AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
            => SettleAsync(message, sdkReceiver => sdkReceiver.AbandonMessageAsync(message, propertiesToModify));

        public Task<ServiceBusSettlementOutcome> DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
            => SettleAsync(message, sdkReceiver => sdkReceiver.DeadLetterMessageAsync(message, deadLetterReason, deadLetterErrorDescription));

        /// <summary>
        /// Ends <paramref name="message"/>'s renewal without settling it. This is the ONLY guaranteed stop for a
        /// delivery whose handler threw: no settle member need have run at all on that path.
        /// </summary>
        public void DeliveryReleased(ServiceBusReceivedMessage message) => _renewals.Stop(message);

        // The ONE settlement funnel. Renewal ends BEFORE the settle call reaches the SDK, so a new settle path
        // cannot be added without also ending renewal. A renewal already awaiting the broker may still land after
        // the cancellation; RenewMessageLockAsync answers an already-settled delivery with the same
        // ServiceBusException(MessageLockLost) an expired lock does, which the renewal loop already absorbs.
        private async Task<ServiceBusSettlementOutcome> SettleAsync(ServiceBusReceivedMessage message, Func<SdkServiceBusReceiver, Task> settleAsync)
        {
            _renewals.Stop(message);
            await settleAsync(InnerReceiver).ConfigureAwait(false);
            return ServiceBusSettlementOutcome.Settled;
        }

        public async Task CloseAsync()
        {
            // Awaited BEFORE the SDK receiver closes, so no renewal outlives the receiver it renews against.
            await _renewals.CloseAsync().ConfigureAwait(false);

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
