using Azure.Core;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Options
{
    public partial class ServiceBusOptionsBuilder
    {
        public IServiceCollection Services { get; private set; }
        private TokenCredential _tokenCredential;
        private const string _defaultAzureServiceBusSectionName = "Chatter:Infrastructure:AzureServiceBus";
        private string _connectionString = null;
        private string _azureServiceBusSectionName = null;
        private IConfiguration _configuration;
        private int _maxConcurrentCalls = _defaultMaxConcurrentCalls;
        private int _prefetchCount = _defaultPrefetchCount;
        private ServiceBusRetryOptions _retryOptions = null;
        private IConfigurationSection _serviceBusOptionsSection = null;
        // INVARIANT: null means WithCrossEntityTransactions was never called, so the config-bound value is
        // left untouched. A non-null value means the fluent method was called and its value overrides any
        // config-bound value in EITHER direction (explicit false overrides config true, explicit true
        // overrides config false). This distinguishes "fluent method not called" from "called with the
        // default value" — a plain bool defaulting to false could not tell those apart, which silently
        // dropped an explicit WithCrossEntityTransactions(false) when config bound true.
        private bool? _enableCrossEntityTransactions = null;

        private const int _defaultMaxConcurrentCalls = 1;
        private const int _defaultPrefetchCount = 0;

        public static ServiceBusOptionsBuilder Create(IServiceCollection services, IConfiguration configuration)
            => new ServiceBusOptionsBuilder(services, configuration);

        private ServiceBusOptionsBuilder(IServiceCollection services, IConfiguration configuration)
        {
            _configuration = configuration;
            Services = services;
            _tokenCredential = null;
            UseConfig();
        }

        public ServiceBusOptionsBuilder AddTokenProvider(TokenCredential tokenCredential)
        {
            _tokenCredential = tokenCredential;
            return this;
        }

        public ServiceBusOptionsBuilder AddTokenProvider(Func<TokenCredential> tokenCredentialFactory)
        {
            _tokenCredential = tokenCredentialFactory?.Invoke();
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

        // Opts the shared ServiceBusClient into cross-entity transactions. Default is OFF: enabling this pins
        // the client to a single top-level entity, so a host configured this way may register only one
        // top-level receiver entity (enforced by a startup guard). A FullAtomicityViaInfrastructure receiver
        // turns this on automatically; call this only to force it on for an explicitly single-entity host.
        public ServiceBusOptionsBuilder WithCrossEntityTransactions(bool enabled = true)
        {
            _enableCrossEntityTransactions = enabled;
            return this;
        }

        public ServiceBusOptionsBuilder WithNoRetry()
        {
            _retryOptions = new ServiceBusRetryOptions { MaxRetries = 0 };
            return this;
        }

        public ServiceBusOptionsBuilder WithExponentialDelay(int maximumRetryCount, double maximumBackoffInSeconds, double minimumBackoffInSeconds, double deltaBackoffInSeconds)
        {
            // Azure.Messaging.ServiceBus has no per-attempt delta-backoff knob; Delay is the base
            // backoff applied exponentially. minimumBackoff maps to Delay, maximumBackoff to MaxDelay.
            // deltaBackoffInSeconds is retained on the signature for source compatibility but has no
            // equivalent in the new SDK retry model.
            _ = deltaBackoffInSeconds;
            _retryOptions = new ServiceBusRetryOptions
            {
                Mode = ServiceBusRetryMode.Exponential,
                MaxRetries = maximumRetryCount,
                Delay = TimeSpan.FromSeconds(minimumBackoffInSeconds),
                MaxDelay = TimeSpan.FromSeconds(maximumBackoffInSeconds)
            };
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
                serviceBusConfig.RetryOptions = new ServiceBusRetryOptions();
            }
            else if (serviceBusConfig.RetryPolicy.MaximumRetryCount == 0
                && serviceBusConfig.RetryPolicy.MaximumBackoffInSeconds == 0
                && serviceBusConfig.RetryPolicy.MinimumBackoffInSeconds == 0
                && serviceBusConfig.RetryPolicy.DeltaBackoffInSeconds == 0)
            {
                serviceBusConfig.RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0 };
            }
            else
            {
                serviceBusConfig.RetryOptions = new ServiceBusRetryOptions
                {
                    Mode = ServiceBusRetryMode.Exponential,
                    MaxRetries = serviceBusConfig.RetryPolicy.MaximumRetryCount,
                    Delay = TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.MinimumBackoffInSeconds),
                    MaxDelay = TimeSpan.FromSeconds(serviceBusConfig.RetryPolicy.MaximumBackoffInSeconds)
                };
            }
        }

        // INVARIANT: connection-string SAS auth is present when the connection string carries either a
        // SharedAccessSignature (pre-signed SAS token) or a key-based SAS pair (SharedAccessKeyName AND
        // SharedAccessKey). The connection string is PARSED into its fields via
        // ServiceBusConnectionStringProperties rather than substring-matched on the raw string: a raw
        // IndexOf("SharedAccessKey") also matches the SharedAccessKeyName key, so an endpoint-only string
        // carrying SharedAccessKeyName (intended to pair with a TokenCredential for AAD) but no actual
        // SharedAccessKey/SharedAccessSignature secret would be falsely detected as SAS, dropping the
        // credential and falling back to a secret-less connection-string auth that cannot connect.
        private static bool ConnectionStringHasSas(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return false;
            }

            ServiceBusConnectionStringProperties properties;
            try
            {
                properties = ServiceBusConnectionStringProperties.Parse(connectionString);
            }
            catch (FormatException)
            {
                return false;
            }

            var hasSharedAccessSignature = !string.IsNullOrWhiteSpace(properties.SharedAccessSignature);
            var hasSharedAccessKeyPair = !string.IsNullOrWhiteSpace(properties.SharedAccessKeyName)
                && !string.IsNullOrWhiteSpace(properties.SharedAccessKey);

            return hasSharedAccessSignature || hasSharedAccessKeyPair;
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

            if (_retryOptions != null)
            {
                options.RetryOptions = _retryOptions;
            }

            if (_tokenCredential != null && !ConnectionStringHasSas(options.ConnectionString))
            {
                options.TokenCredential = _tokenCredential;
            }

            if (_maxConcurrentCalls != _defaultMaxConcurrentCalls)
            {
                options.MaxConcurrentCalls = _maxConcurrentCalls;
            }

            if (_prefetchCount != _defaultPrefetchCount)
            {
                options.PrefetchCount = _prefetchCount;
            }

            // Explicit fluent call wins over configuration: apply the fluent value only when
            // WithCrossEntityTransactions was actually called, leaving the config-bound value untouched
            // otherwise.
            if (_enableCrossEntityTransactions.HasValue)
            {
                options.EnableCrossEntityTransactions = _enableCrossEntityTransactions.Value;
            }

            Services.AddSingleton(options);

            return options;
        }
    }
}
