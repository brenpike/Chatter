using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Saga;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// The message received by the <see cref="BrokeredMessageReceiver{TMessage}"/>
    /// </summary>
    public class InboundBrokeredMessage
    {
        private readonly IDictionary<string, object> _applicationProperties;

        internal InboundBrokeredMessage(string messageId, byte[] body, IDictionary<string, object> applicationProperties, string messageReceiverPath, IBrokeredMessageBodyConverter bodyConverter)
        {
            MessageId = messageId ?? throw new System.ArgumentNullException(nameof(messageId));
            Body = body ?? throw new System.ArgumentNullException(nameof(body));
            _applicationProperties = applicationProperties ?? new ConcurrentDictionary<string, object>();
            MessageReceiverPath = messageReceiverPath;
            BodyConverter = bodyConverter ?? throw new System.ArgumentNullException(nameof(bodyConverter));
            TransactionMode = GetTransactionMode();
            CorrelationId = GetApplicationPropertyByKey<string>(Headers.CorrelationId);
        }

        /// <summary>
        /// The message id of the received message
        /// </summary>
        public string MessageId { get; }
        /// <summary>
        /// The body of the received message
        /// </summary>
        public byte[] Body { get; }
        /// <summary>
        /// The application properties of the received message
        /// </summary>
        public IReadOnlyDictionary<string, object> ApplicationProperties => (IReadOnlyDictionary<string, object>)_applicationProperties;
        /// <summary>
        /// The name of the message receiver that recieved this message
        /// </summary>
        public string MessageReceiverPath { get; }
        /// <summary>
        /// The correlation id of the received message
        /// </summary>
        public string CorrelationId { get; }
        /// <summary>
        /// The mode of the transaction this message is participating in
        /// </summary>
        public TransactionMode TransactionMode { get; }
        /// <summary>
        /// Will be true once the <see cref="InboundBrokeredMessage"/> has been successfully received (completed). The message must be successfully handled 
        /// and routed optionally to the next and reply destination(s).
        /// NOTE: This can never be true in the received message handler.
        /// </summary>
        public bool SuccessfullyReceived { get; internal set; }
        /// <summary>
        /// True if the inbound message has encountered an error while being received
        /// </summary>
        public bool IsError => GetApplicationPropertyByKey<bool>(Headers.IsError);
        /// <summary>
        /// True if the inbound message has not encountered an error while being received
        /// </summary>
        public bool IsSuccess => !IsError;
        /// <summary>
        /// The receivers visited by the inbound message prior to the most recent message receiver
        /// </summary>
        public string Via => GetApplicationPropertyByKey<string>(Headers.Via);
        internal IBrokeredMessageBodyConverter BodyConverter { get; }

        /// <summary>
        /// Gets a message of type <typeparamref name="TBody"/> from the message body
        /// </summary>
        /// <typeparam name="TBody">The type of the object stored as the message body</typeparam>
        /// <returns>The strongly typed message payload</returns>
        public TBody GetMessageFromBody<TBody>()
            => this.BodyConverter.Convert<TBody>(this.Body);

        internal InboundBrokeredMessage UpdateVia(string via)
        {
            var key = Headers.Via;
            if (_applicationProperties.ContainsKey(key))
            {
                var currentVia = (string)ApplicationProperties[key];
                if (!(string.IsNullOrWhiteSpace(via)))
                {
                    currentVia += "," + via;
                    _applicationProperties[key] = currentVia;
                }
            }
            else
            {
                _applicationProperties[key] = via;
            }
            return this;
        }

        private TransactionMode GetTransactionMode()
        {
            if (_applicationProperties.TryGetValue(Headers.TransactionMode, out var transactionMode))
            {
                return (TransactionMode)transactionMode;
            }
            else
            {
                return TransactionMode.FullAtomicityViaInfrastructure;
            }
        }

        private T GetApplicationPropertyByKey<T>(string key)
        {
            if (_applicationProperties.TryGetValue(key, out var output))
            {
                return (T)output;
            }
            else
            {
                return default;
            }
        }

        internal InboundBrokeredMessage WithFailureDetails(string failureDetails)
        {
            _applicationProperties[Headers.FailureDetails] = failureDetails;
            return this;
        }

        internal InboundBrokeredMessage WithFailureDescription(string failureDescription)
        {
            _applicationProperties[Headers.FailureDescription] = failureDescription;
            return this;
        }

        internal InboundBrokeredMessage SetFailure()
        {
            _applicationProperties[Headers.IsError] = true;
            _applicationProperties[Headers.SagaStatus] = (byte)SagaStatusEnum.Failed;
            return this;
        }

        internal InboundBrokeredMessage WithSagaStatus(SagaStatusEnum sagaStatus)
        {
            _applicationProperties[Headers.SagaStatus] = (byte)sagaStatus;
            return this;
        }
    }
}
