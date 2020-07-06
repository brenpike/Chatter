using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Saga;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Receiving
{
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
            ReplyTo = GetApplicationPropertyByKey<string>(Headers.ReplyTo);
            ReplyToGroupId = GetApplicationPropertyByKey<string>(Headers.ReplyToGroupId);
            GroupId = GetApplicationPropertyByKey<string>(Headers.GroupId);
            TransactionMode = GetTransactionMode();
            CorrelationId = GetApplicationPropertyByKey<string>(Headers.CorrelationId);
        }

        public string MessageId { get; }
        public byte[] Body { get; }
        public IReadOnlyDictionary<string, object> ApplicationProperties => (IReadOnlyDictionary<string, object>)_applicationProperties;
        public string MessageReceiverPath { get; }
        public string ReplyTo { get; }
        public string ReplyToGroupId { get; }
        public string GroupId { get; }
        public string CorrelationId { get; }
        public TransactionMode TransactionMode { get; }

        /// <summary>
        /// Will be true once the <see cref="InboundBrokeredMessage"/> has been successfully received (completed). The message must be successfully handled 
        /// and routed optionally to the next and reply destination(s).
        /// NOTE: This can never be true in the received message handler.
        /// </summary>
        public bool SuccessfullyReceived { get; internal set; }

        public bool IsError => GetApplicationPropertyByKey<bool>(Headers.IsError);
        public bool IsSuccess => !IsError;
        public string Via => GetApplicationPropertyByKey<string>(Headers.Via);
        internal IBrokeredMessageBodyConverter BodyConverter { get; }

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
                return TransactionMode.FullAtomicity;
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

        internal InboundBrokeredMessage SetError()
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
