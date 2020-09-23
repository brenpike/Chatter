using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    internal sealed class BrokeredMessageOutboxProcessor : BackgroundService
    {
        private readonly IBrokeredMessageInfrastructureDispatcher _brokeredMessageInfrastructureDispatcher;
        private readonly ILogger<BrokeredMessageOutboxProcessor> _logger;
        private readonly ReliabilityOptions _reliabilityOptions;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public BrokeredMessageOutboxProcessor(IBrokeredMessageInfrastructureDispatcher brokeredMessageInfrastructureDispatcher,
                                              ILogger<BrokeredMessageOutboxProcessor> logger,
                                              ReliabilityOptions reliabilityOptions,
                                              IServiceScopeFactory serviceScopeFactory)
        {
            _brokeredMessageInfrastructureDispatcher = brokeredMessageInfrastructureDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageInfrastructureDispatcher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reliabilityOptions = reliabilityOptions ?? throw new ArgumentNullException(nameof(reliabilityOptions));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogDebug($"BrokeredMessageOutboxProcessor is starting.");

            stoppingToken.Register(() =>
                _logger.LogDebug($" BrokeredMessageOutboxProcessor background task is stopping."));

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogTrace($"BrokeredMessageOutboxProcessor is now processing messages...");

                await SendOutboxMessagesAsync();

                await Task.Delay(_reliabilityOptions.OutboxIntervalInMilliseconds, stoppingToken);
            }

            _logger.LogDebug($"BrokeredMessageOutboxProcessor background task is stopping.");
        }

        private async Task SendOutboxMessagesAsync()
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<ITransactionalBrokeredMessageOutbox>();
            var bodyConverterFactory = scope.ServiceProvider.GetRequiredService<IBodyConverterFactory>();
            var messages = await outbox.GetUnprocessedBrokeredMessagesFromOutbox();

            if (!messages.Any())
            {
                _logger.LogTrace($"No messages available for processing in outbox.");
                return;
            }

            _logger.LogTrace($"{messages.Count()} messages available for processing in outbox.");

            foreach (var message in messages.OrderBy(m => m.SentToOutboxAtUtc))
            {
                //TODO: clean up this mess with app properties. extension methods?
                //TODO: move this processing piece to ITransactionalBrokeredMessageOutbox. Make it configurable to dispatch from outbox on demand, 
                //      i.e., right after saving to outbox, we process from outbox.
                //      once this is accomplished we can have separate config settings to have the timed processor running and the on-domand processor.
                //      on demand processor needs to happen outside of transaction or can it happen in the same transaction? (prod different, as needs to be
                //      committed first, duh)
                var appProps = JsonConvert.DeserializeObject<IDictionary<string, object>>(message.StringifiedApplicationProperties);
                appProps.TryGetValue(ApplicationProperties.ContentType, out var contentType);
                var outbound = message.AsOutboundBrokeredMessage(bodyConverterFactory.CreateBodyConverter((string)contentType));
                _logger.LogTrace($"Processing message '{message.MessageId}' from outbox.");

                await _brokeredMessageInfrastructureDispatcher.Dispatch(outbound, null);
                _logger.LogTrace($"Message '{message.MessageId}' dispatched to messaging infrastructure from outbox.");

                await outbox.MarkMessageAsProcessed(message);
            }
        }
    }
}
