using Chatter.CQRS.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker
{
    public class TableWatcher<TMessageData> : BackgroundService where TMessageData : class, IEvent
    {
        private readonly ILogger<TableWatcher<TMessageData>> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public TableWatcher(ILogger<TableWatcher<TMessageData>> logger, IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var serviceBrokerReceiver = scope.ServiceProvider.GetRequiredService<SqlServiceBrokerReceiver<TMessageData>>();
            _logger.LogInformation("Starting sql table notifier.");
            using var _ = await serviceBrokerReceiver.Start();
            _logger.LogInformation("Stopping sql table notifier.");
        }
    }
}
