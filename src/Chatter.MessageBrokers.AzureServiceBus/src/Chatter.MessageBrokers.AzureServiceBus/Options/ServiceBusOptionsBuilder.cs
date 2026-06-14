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
        // INVARIANT: null means WithMaxConcurrentCalls was never called, so the config-bound value is
        // left untouched. A non-null value means the fluent method was called and its value overrides any
        // config-bound value in EITHER direction (explicit 1 overrides config 5, explicit 0 overrides
        // config 1). This distinguishes "fluent method not called" from "called with the default value"
        // — a plain int defaulting to 1 could not tell those apart, silently dropping an explicit
        // WithMaxConcurrentCalls(1) when config bound something else.
        private int? _maxConcurrentCalls = null;
        // INVARIANT: null means WithPrefetchCount was never called, so the config-bound value is
        // left untouched. A non-null value means the fluent method was called and its value overrides
        // any config-bound value in EITHER direction (explicit 0 overrides config 10).
        private int? _prefetchCount = null;
        private ServiceBusRetryOptions _retryOptions = null;
        private IConfigurationSection _serviceBusOptionsSection = null;
        // INVARIANT: null means WithCrossEntityTransactions was never called, so the config-bound value is
        // left untouched. A non-null value means the fluent method was called and its value overrides any
        // config-bound value in EITHER direction (explicit false overrides config true, explicit true
        // overrides config false). This distinguishes "fluent method not called" from "called with the
        // default value" — a plain bool defaulting to false could not tell those apart, which silently
        // dropped an explicit WithCrossEntityTransactions(false) when config bound true.
        private bool? _enableCrossEntityTransactions = null;
        // INVARIANT: null means WithSessionIdleTimeout was never called, so the config-bound value is
        // left untouched. A non-null value means the fluent method was called and its value overrides
        // any config-bound value in EITHER direction (explicit 30 s overrides config 120 s).
        private TimeSpan? _sessionIdleTimeout = null;
        // INVARIANT: null means WithMaxSessionLockRenewalDuration was never called, so the config-bound
        // value is left untouched. A non-null value means the fluent method was called and its value
        // overrides any config-bound value in EITHER direction (explicit 2 min overrides config 10 min).
        private TimeSpan? _maxSessionLockRenewalDuration = null;

        private const int _defaultMaxConcurrentCalls = 1;
        private const int _defaultPrefetchCount = 0;
        private static readonly TimeSpan _defaultSessionIdleTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan _defaultMaxSessionLockRenewalDuration = TimeSpan.FromMinutes(5);

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

        /// <summary>
        /// Overrides how long a held session may yield no message before it is released and the
        /// receiver rolls to the next session. Applies only to session-enabled receivers.
        /// Default: 60 seconds.
        /// </summary>
        public ServiceBusOptionsBuilder WithSessionIdleTimeout(TimeSpan timeout)
        {
            _sessionIdleTimeout = timeout;
            return this;
        }

        /// <summary>
        /// Overrides the ceiling on how long a held session's lock is renewed for long-running
        /// processing. Once reached, renewal stops and the session is allowed to expire or roll
        /// naturally. Applies only to session-enabled receivers. Default: 5 minutes.
        /// </summary>
        public ServiceBusOptionsBuilder WithMaxSessionLockRenewalDuration(TimeSpan duration)
        {
            _maxSessionLockRenewalDuration = duration;
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

            // Explicit fluent call wins over configuration: apply the fluent value only when
            // WithMaxConcurrentCalls was actually called, leaving the config-bound value untouched
            // otherwise.
            if (_maxConcurrentCalls.HasValue)
            {
                options.MaxConcurrentCalls = _maxConcurrentCalls.Value;
            }

            // Explicit fluent call wins over configuration: apply the fluent value only when
            // WithPrefetchCount was actually called, leaving the config-bound value untouched
            // otherwise.
            if (_prefetchCount.HasValue)
            {
                options.PrefetchCount = _prefetchCount.Value;
            }

            // Explicit fluent call wins over configuration: apply the fluent value only when
            // WithCrossEntityTransactions was actually called, leaving the config-bound value untouched
            // otherwise.
            if (_enableCrossEntityTransactions.HasValue)
            {
                options.EnableCrossEntityTransactions = _enableCrossEntityTransactions.Value;
            }

            // Explicit fluent call wins over configuration: apply the fluent value only when
            // WithSessionIdleTimeout was actually called, leaving the config-bound value untouched
            // otherwise.
            if (_sessionIdleTimeout.HasValue)
            {
                options.SessionIdleTimeout = _sessionIdleTimeout.Value;
            }

            // Explicit fluent call wins over configuration: apply the fluent value only when
            // WithMaxSessionLockRenewalDuration was actually called, leaving the config-bound value
            // untouched otherwise.
            if (_maxSessionLockRenewalDuration.HasValue)
            {
                options.MaxSessionLockRenewalDuration = _maxSessionLockRenewalDuration.Value;
            }

            Services.AddSingleton(options);

            return options;
        }
    }
}
