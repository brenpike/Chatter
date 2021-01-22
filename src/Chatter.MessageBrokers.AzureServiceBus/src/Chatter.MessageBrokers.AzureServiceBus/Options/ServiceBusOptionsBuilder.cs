using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Options
{
    public partial class ServiceBusOptionsBuilder
    {
        public IServiceCollection Services { get; private set; }
        private ITokenProvider _tokenProvider;
        private const string _defaultAzureServiceBusSectionName = "Chatter:Infrastructure:AzureServiceBus";
        private string _connectionString = null;
        private string _azureServiceBusSectionName = null;
        private IConfiguration _configuration;
        private int _maxConcurrentCalls = _defaultMaxConcurrentCalls;
        private int _prefetchCount = _defaultPrefetchCount;
        private RetryPolicy _retryPolicy = RetryPolicy.Default;
        private IConfigurationSection _serviceBusOptionsSection = null;

        private const int _defaultMaxConcurrentCalls = 1;
        private const int _defaultPrefetchCount = 0;

        public static ServiceBusOptionsBuilder Create(IServiceCollection services, IConfiguration configuration)
            => new ServiceBusOptionsBuilder(services, configuration);

        private ServiceBusOptionsBuilder(IServiceCollection services, IConfiguration configuration)
        {
            _configuration = configuration;
            Services = services;
            _tokenProvider = new NullTokenProvider();
            UseConfig();
        }

        public ServiceBusOptionsBuilder AddTokenProvider(Func<ITokenProvider> tokenProviderFactory)
        {
            _tokenProvider = tokenProviderFactory?.Invoke() ?? new NullTokenProvider();
            return this;
        }

        public ServiceBusOptionsBuilder UseConfig(string configSectionName = _defaultAzureServiceBusSectionName)
        {
            _azureServiceBusSectionName = configSectionName;
            _serviceBusOptionsSection = _configuration.GetSection(configSectionName);
            return this;
        }

        public ServiceBusOptionsBuilder WithConnectionString(string connectionString)
        {
            _connectionString = connectionString;
            return this;
        }

        public ServiceBusOptionsBuilder WithMaxConcurrentCalls(int maxConcurrentCalls)
        {
            _maxConcurrentCalls = maxConcurrentCalls;
            return this;
        }

        public ServiceBusOptionsBuilder WithPrefetchCount(int count)
        {
            _prefetchCount = count;
            return this;
        }

        public ServiceBusOptionsBuilder WithNoDelay()
        {
            _retryPolicy = RetryPolicy.NoRetry;
            return this;
        }

        public ServiceBusOptionsBuilder WithExponentialDelay(int maximumRetryCount, double maximumBackoffInSeconds, double minimumBackoffInSeconds, double deltaBackoffInSeconds)
        {
            _retryPolicy = new RetryExponential(TimeSpan.FromSeconds(minimumBackoffInSeconds),
                            TimeSpan.FromSeconds(maximumBackoffInSeconds),
                            TimeSpan.FromSeconds(deltaBackoffInSeconds),
                            maximumRetryCount);
            return this;
        }

        private void PostConfiguration(ServiceBusOptions serviceBusConfig)
        {
            if (serviceBusConfig == null)
            {
                return;
            }

            if (serviceBusConfig.RetryPolicy == null)
            {
                serviceBusConfig.Policy = RetryPolicy.Default;
            }
            else if (serviceBusConfig.RetryPolicy.MaximumRetryCount == 0
                && serviceBusConfig.RetryPolicy.MaximumBackoffInSeconds == 0
                && serviceBusConfig.RetryPolicy.MinimumBackoffInSeconds == 0
                && serviceBusConfig.RetryPolicy.DeltaBackoffInSeconds == 0)
            {
                serviceBusConfig.Policy = RetryPolicy.NoRetry;
            }
            else
            {
                var retryExponential = new RetryExponential(TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.MinimumBackoffInSeconds),
                                            TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.MaximumBackoffInSeconds),
                                            TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.DeltaBackoffInSeconds),
                                            serviceBusConfig.RetryPolicy.MaximumRetryCount);
                serviceBusConfig.Policy = retryExponential;
            }

            serviceBusConfig.TokenProvider = _tokenProvider;
        }

        internal ServiceBusOptions Build()
        {
            var options = new ServiceBusOptions();
            if (_azureServiceBusSectionName != null && _serviceBusOptionsSection.Exists())
            {
                options = _serviceBusOptionsSection.Get<ServiceBusOptions>();
                PostConfiguration(options);
            }

            if (string.IsNullOrWhiteSpace(_connectionString) && string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new Exception("A connection string is required.");
            }

            if (!string.IsNullOrWhiteSpace(_connectionString))
            {
                options.ConnectionString = _connectionString;
            }

            if (_retryPolicy != RetryPolicy.Default)
            {
                options.Policy = _retryPolicy;
            }

            if (!(_tokenProvider is NullTokenProvider))
            {
                options.TokenProvider = _tokenProvider;
            }

            if (_maxConcurrentCalls != _defaultMaxConcurrentCalls)
            {
                options.MaxConcurrentCalls = _maxConcurrentCalls;
            }

            if (_prefetchCount != _defaultPrefetchCount)
            {
                options.PrefetchCount = _prefetchCount;
            }

            Services.AddSingleton(options);

            return options;
        }
    }
}
