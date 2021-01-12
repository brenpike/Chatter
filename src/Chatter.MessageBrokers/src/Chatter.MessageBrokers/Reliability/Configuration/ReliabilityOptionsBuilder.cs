using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.Reliability.Configuration
{
    public class ReliabilityOptionsBuilder
    {
        private bool _routeMessagesToOutbox = false;
        private double _minutesToLiveInMemory = 10;
        private bool _enableOutboxPollingProcessor = false;
        private int _outboxProcessingIntervalInMilliseconds = 5000;

        public const string ReliabilityOptionsSectionName = "Chatter:MessageBrokers:Reliability";
        private readonly IServiceCollection _services;
        private readonly IConfiguration _configuration;
        private readonly IConfigurationSection _reliabilityOptionsSection;

        public static ReliabilityOptionsBuilder Create(IServiceCollection services)
            => new ReliabilityOptionsBuilder(services);

        private ReliabilityOptionsBuilder(IServiceCollection services) : this(services, null, null) { }
        private ReliabilityOptionsBuilder(IServiceCollection services, IConfiguration configuration, IConfigurationSection section)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _configuration = configuration;
            _reliabilityOptionsSection = section;
        }

        public static ReliabilityOptions FromConfig(IServiceCollection services, IConfiguration configuration, string reliabilityOptionsSectionName = ReliabilityOptionsSectionName)
        {
            var section = configuration?.GetSection(reliabilityOptionsSectionName);
            var builder = new ReliabilityOptionsBuilder(services, configuration, section);
            return builder.Build();
        }

        public ReliabilityOptionsBuilder WithOutboxRouting()
        {
            _routeMessagesToOutbox = true;
            return this;
        }

        public ReliabilityOptionsBuilder WithInMemoryOutboxTimeToLive(double timeToLiveInMinutes)
        {
            _minutesToLiveInMemory = timeToLiveInMinutes;
            return this;
        }

        public ReliabilityOptionsBuilder WithOutboxPollingProcessor(int outboxProcessingIntervalInMilliseconds = 5000)
        {
            _enableOutboxPollingProcessor = true;
            _outboxProcessingIntervalInMilliseconds = outboxProcessingIntervalInMilliseconds;
            return this;
        }

        public ReliabilityOptions Build()
        {
            var reliabilityOptions = new ReliabilityOptions();
            if (_reliabilityOptionsSection != null && _reliabilityOptionsSection.Exists())
            {
                reliabilityOptions = _reliabilityOptionsSection.Get<ReliabilityOptions>();
                _services.Configure<ReliabilityOptions>(_reliabilityOptionsSection);
            }
            else
            {
                reliabilityOptions.RouteMessagesToOutbox = _routeMessagesToOutbox;
                reliabilityOptions.MinutesToLiveInMemory = _minutesToLiveInMemory;
                reliabilityOptions.EnableOutboxPollingProcessor = _enableOutboxPollingProcessor;
                reliabilityOptions.OutboxProcessingIntervalInMilliseconds = _outboxProcessingIntervalInMilliseconds;
            }

            _services.AddSingleton(reliabilityOptions);

            return reliabilityOptions;
        }
    }
}
