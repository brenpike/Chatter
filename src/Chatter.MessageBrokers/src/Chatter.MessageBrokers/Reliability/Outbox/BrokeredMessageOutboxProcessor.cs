using Chatter.MessageBrokers.Reliability.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    internal sealed class BrokeredMessageOutboxProcessor : BackgroundService
    {
        private readonly ILogger<BrokeredMessageOutboxProcessor> _logger;
        private readonly ReliabilityOptions _reliabilityOptions;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public BrokeredMessageOutboxProcessor(ILogger<BrokeredMessageOutboxProcessor> logger,
                                              ReliabilityOptions reliabilityOptions,
                                              IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reliabilityOptions = reliabilityOptions ?? throw new ArgumentNullException(nameof(reliabilityOptions));
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"BrokeredMessageOutboxProcessor is starting.");

            stoppingToken.Register(() =>
                _logger.LogDebug($" BrokeredMessageOutboxProcessor background task is stopping."));

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogTrace($"BrokeredMessageOutboxProcessor is now processing messages...");

                await SendOutboxMessagesAsync(stoppingToken);

                await Task.Delay(_reliabilityOptions.OutboxProcessingIntervalInMilliseconds, stoppingToken);
            }

            _logger.LogInformation($"BrokeredMessageOutboxProcessor background task is stopping.");
        }

        private async Task SendOutboxMessagesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var outbox = scope.ServiceProvider.GetRequiredService<IBrokeredMessageOutbox>();
                var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
                var messages = await outbox.GetUnprocessedMessagesFromOutbox(cancellationToken);

                if (!messages.Any())
                {
                    _logger.LogTrace($"No messages available for processing in outbox.");
                    return;
                }

                _logger.LogTrace($"{messages.Count()} messages available for processing in outbox.");

                foreach (var message in messages.OrderBy(m => m.SentToOutboxAtUtc))
                {
                    await processor.Process(message, cancellationToken);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error sending outbox messages");
            }
        }
    }
}
