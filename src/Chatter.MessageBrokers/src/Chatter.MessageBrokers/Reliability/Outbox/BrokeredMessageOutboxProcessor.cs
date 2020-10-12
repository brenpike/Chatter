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
        private readonly IMessagingInfrastructureDispatcher _brokeredMessageInfrastructureDispatcher;
        private readonly ILogger<BrokeredMessageOutboxProcessor> _logger;
        private readonly ReliabilityOptions _reliabilityOptions;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public BrokeredMessageOutboxProcessor(IMessagingInfrastructureDispatcher brokeredMessageInfrastructureDispatcher,
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

                await Task.Delay(_reliabilityOptions.OutboxProcessingIntervalInMilliseconds, stoppingToken);
            }

            _logger.LogDebug($"BrokeredMessageOutboxProcessor background task is stopping.");
        }

        private async Task SendOutboxMessagesAsync()
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>();
            var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
            var messages = await outbox.GetUnprocessedMessagesFromOutbox();

            if (!messages.Any())
            {
                _logger.LogTrace($"No messages available for processing in outbox.");
                return;
            }

            _logger.LogTrace($"{messages.Count()} messages available for processing in outbox.");

            foreach (var message in messages.OrderBy(m => m.SentToOutboxAtUtc))
            {
                await processor.Process(message).ConfigureAwait(false);
            }
        }
    }
}
