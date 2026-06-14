using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public class OutboxProcessor : IOutboxProcessor
    {
        private readonly IMessagingInfrastructureProvider _infrastructureProvider;
        private readonly ILogger<OutboxProcessor> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IBrokeredMessageOutbox _brokeredMessageOutbox;

        public OutboxProcessor(IMessagingInfrastructureProvider infrastructureProvider,
                               ILogger<OutboxProcessor> logger,
                               IBodyConverterFactory bodyConverterFactory,
                               IBrokeredMessageOutbox brokeredMessageOutbox)
        {
            _infrastructureProvider = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _brokeredMessageOutbox = brokeredMessageOutbox ?? throw new ArgumentNullException(nameof(brokeredMessageOutbox));
        }

        public async Task Process(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            try
            {
                // The persisted MessageContext is a JSON string whose values are NOT all strings:
                // WithTimeToLive/RefreshTimeToLive write a TimeSpan, Azure Service Bus
                // WithScheduledEnqueueTimeUtc writes a DateTime, and SSB receive/deadletter paths write an
                // integer ReceiveAttempts. MaterializePersistedContext deserializes the string through
                // ChatterJson.Options, where the registered MaterializingObjectConverter restores inline the
                // CLR types Newtonsoft's untyped read produced — so the (string)/(DateTime?)/integer reads on
                // the replayed context below and downstream remain correct.
                IDictionary<string, object> messageContext = MessageContext.MaterializePersistedContext(message.MessageContext);

                var contentType = message.MessageContentType;
                if (string.IsNullOrWhiteSpace(message.MessageContentType))
                {
                    contentType = (string)messageContext[MessageContext.ContentType];
                    _logger.LogTrace($"Outbox message did not contain content type. Retrieved from message context.");
                }

                messageContext.TryGetValue(MessageContext.InfrastructureType, out var infra);
                var dispatcherInfrastructure = _infrastructureProvider.GetDispatcher((string)infra);

                if (string.IsNullOrWhiteSpace(contentType))
                {
                    _logger.LogTrace($"No content type set in outbox message or message context. Unable to dispatch message.");
                    throw new ArgumentNullException(nameof(contentType), "A content type is required to serialize and send brokered message.");
                }

                var converter = _bodyConverterFactory.CreateBodyConverter(contentType);

                var outbound = new OutboundBrokeredMessage(message.MessageId, converter.GetBytes(message.MessageBody), messageContext, message.Destination, converter);
                _logger.LogTrace($"Processing message '{message.MessageId}' from outbox.");

                var pollable = (IPollableOutboxStore)_brokeredMessageOutbox;
                await ((IUnitOfWork)_brokeredMessageOutbox).ExecuteAsync(async ct =>
                {
                    await pollable.UpdateProcessedDate(message, ct);

                    await dispatcherInfrastructure.Dispatch(outbound, null);
                    _logger.LogTrace($"Message '{message.MessageId}' dispatched to messaging infrastructure from outbox.");

                }, null, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Unable to process outbox message with id '{message.Id}'");
            }
        }

        public async Task ProcessBatch(Guid batchId, CancellationToken cancellationToken = default)
        {
            var pollable = (IPollableOutboxStore)_brokeredMessageOutbox;
            var messages = await pollable.GetUnprocessedBatch(batchId, cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"Processing '{messages.Count()}' messages for batch '{batchId}'.");

            foreach (var message in messages)
            {
                await Process(message, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
