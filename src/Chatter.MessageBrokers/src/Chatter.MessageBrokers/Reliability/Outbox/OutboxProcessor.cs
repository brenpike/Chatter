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
        private readonly IBrokeredMessageInfrastructureDispatcher _brokeredMessageInfrastructureDispatcher;
        private readonly ILogger<OutboxProcessor> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IBrokeredMessageOutbox _brokeredMessageOutbox;

        public OutboxProcessor(IBrokeredMessageInfrastructureDispatcher brokeredMessageInfrastructureDispatcher,
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
            var appProps = JsonConvert.DeserializeObject<IDictionary<string, object>>(message.StringifiedApplicationProperties);
            appProps.TryGetValue(ApplicationProperties.ContentType, out var contentType);

            var outbound = message.AsOutboundBrokeredMessage(appProps, _bodyConverterFactory.CreateBodyConverter((string)contentType));
            _logger.LogTrace($"Processing message '{message.MessageId}' from outbox.");

            await _brokeredMessageInfrastructureDispatcher.Dispatch(outbound, null);
            _logger.LogTrace($"Message '{message.MessageId}' dispatched to messaging infrastructure from outbox.");

            await _brokeredMessageOutbox.UpdateProcessedDate(message);
        }

        public async Task ProcessBatch(Guid batchId)
        {
            var messages = await _brokeredMessageOutbox.GetUnprocessedBatch(batchId);
            _logger.LogTrace($"Processing '{messages.Count()}' messages for batch '{batchId}'.");

            foreach (var message in messages)
            {
                await Process(message);
            }
        }
    }
}
