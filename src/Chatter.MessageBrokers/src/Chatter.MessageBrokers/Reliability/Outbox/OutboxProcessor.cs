using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public class OutboxProcessor : IOutboxProcessor
    {
        private readonly IMessagingInfrastructureDispatcher _brokeredMessageInfrastructureDispatcher;
        private readonly ILogger<OutboxProcessor> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IBrokeredMessageOutbox _brokeredMessageOutbox;

        public OutboxProcessor(IMessagingInfrastructureDispatcher brokeredMessageInfrastructureDispatcher,
                               ILogger<OutboxProcessor> logger,
                               IBodyConverterFactory bodyConverterFactory,
                               IBrokeredMessageOutbox brokeredMessageOutbox)
        {
            _brokeredMessageInfrastructureDispatcher = brokeredMessageInfrastructureDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageInfrastructureDispatcher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _brokeredMessageOutbox = brokeredMessageOutbox ?? throw new ArgumentNullException(nameof(brokeredMessageOutbox));
        }

        public async Task Process(OutboxMessage message)
        {
            IDictionary<string, object> messageContext = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(message.MessageContext))
            {
                messageContext = JsonConvert.DeserializeObject<IDictionary<string, object>>(message.MessageContext);
            }

            var contentType = message.MessageContentType;
            if (string.IsNullOrWhiteSpace(message.MessageContentType))
            {
                contentType = (string)messageContext[MessageContext.ContentType];
                _logger.LogTrace($"Outbox message did not contain content type. Retrieved from message context.");
            }

            if (string.IsNullOrWhiteSpace(contentType))
            {
                _logger.LogTrace($"No content type set in outbox message or message context. Unable to dispatch message.");
                throw new ArgumentNullException(nameof(contentType), "A content type is required to serialize and send brokered message.");
            }

            var converter = _bodyConverterFactory.CreateBodyConverter(contentType);

            var outbound = new OutboundBrokeredMessage(message.MessageId, converter.GetBytes(message.MessageBody), messageContext, message.Destination, converter);
            _logger.LogTrace($"Processing message '{message.MessageId}' from outbox.");

            await _brokeredMessageInfrastructureDispatcher.Dispatch(outbound, null).ConfigureAwait(false);
            _logger.LogTrace($"Message '{message.MessageId}' dispatched to messaging infrastructure from outbox.");

            await _brokeredMessageOutbox.UpdateProcessedDate(message).ConfigureAwait(false);
        }

        public async Task ProcessBatch(Guid batchId)
        {
            var messages = await _brokeredMessageOutbox.GetUnprocessedBatch(batchId).ConfigureAwait(false);
            _logger.LogTrace($"Processing '{messages.Count()}' messages for batch '{batchId}'.");

            foreach (var message in messages)
            {
                await Process(message).ConfigureAwait(false);
            }
        }
    }
}
