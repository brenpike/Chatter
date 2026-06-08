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
                IDictionary<string, object> messageContext = new Dictionary<string, object>();
                if (!string.IsNullOrWhiteSpace(message.MessageContext))
                {
                    // The persisted MessageContext is an IDictionary<string, object> whose values are
                    // NOT all strings: WithTimeToLive/RefreshTimeToLive write a TimeSpan, Azure Service
                    // Bus WithScheduledEnqueueTimeUtc writes a DateTime, and SSB receive/deadletter paths
                    // write an integer ReceiveAttempts. Deserializing to Dictionary<string, string> threw
                    // on any non-string JSON token (e.g. the numeric ReceiveAttempts), and Process only
                    // logs the exception, silently stranding a valid outbox row. Deserialize to
                    // JsonElement and materialize each value to its CLR primitive by ValueKind so every
                    // valid row replays and the (string) reads below remain correct for string headers.
                    var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(message.MessageContext, ChatterJson.Options);
                    messageContext = headers.ToDictionary(kvp => kvp.Key, kvp => MaterializeContextValue(kvp.Value));
                }

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

                await ((IUnitOfWork)_brokeredMessageOutbox).ExecuteAsync(async ct =>
                {
                    await _brokeredMessageOutbox.UpdateProcessedDate(message, ct);

                    await dispatcherInfrastructure.Dispatch(outbound, null);
                    _logger.LogTrace($"Message '{message.MessageId}' dispatched to messaging infrastructure from outbox.");

                }, null, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Unable to process outbox message with id '{message.Id}'");
            }
        }

        // Materializes a persisted MessageContext value to the CLR primitive that matches its JSON
        // kind, mirroring Newtonsoft's IDictionary<string, object> round-trip. String headers (e.g.
        // ContentType, InfrastructureType) materialize to System.String so existing (string) casts
        // hold; numbers/booleans/null round-trip without forcing a string; objects/arrays (none are
        // written today) fall back to their raw JSON so no value is dropped.
        private static object MaterializeContextValue(System.Text.Json.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    return element.GetString();
                case System.Text.Json.JsonValueKind.Number:
                    return element.TryGetInt64(out var l) ? l : element.GetDouble();
                case System.Text.Json.JsonValueKind.True:
                case System.Text.Json.JsonValueKind.False:
                    return element.GetBoolean();
                case System.Text.Json.JsonValueKind.Null:
                case System.Text.Json.JsonValueKind.Undefined:
                    return null;
                default:
                    return element.GetRawText();
            }
        }

        public async Task ProcessBatch(Guid batchId, CancellationToken cancellationToken = default)
        {
            var messages = await _brokeredMessageOutbox.GetUnprocessedBatch(batchId, cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"Processing '{messages.Count()}' messages for batch '{batchId}'.");

            foreach (var message in messages)
            {
                await Process(message, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
