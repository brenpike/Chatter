using Azure.Core;
using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers.Configuration;
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
        private const string _retryPolicySectionName = "RetryPolicy";
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
            _retryOptions = CreateExponentialRetryOptions(maximumRetryCount, maximumBackoffInSeconds, minimumBackoffInSeconds);
            return this;
        }

        // INVARIANT: this is the ONE construction site for exponential retry options — the fluent
        // WithExponentialDelay setter and the configuration-derived CreateConfiguredRetryOptions both route
        // through it, so both paths bind the same way and the SDK's own setters stay the single authority on
        // which values it can run with.
        // A null backoff means "not configured", leaving the SDK default for that parameter in place.
        private static ServiceBusRetryOptions CreateExponentialRetryOptions(int maximumRetryCount, double? maximumBackoffInSeconds, double? minimumBackoffInSeconds)
        {
            var sdkDefaults = new ServiceBusRetryOptions();
            return new ServiceBusRetryOptions
            {
                Mode = ServiceBusRetryMode.Exponential,
                MaxRetries = maximumRetryCount,
                Delay = minimumBackoffInSeconds.HasValue ? TimeSpan.FromSeconds(minimumBackoffInSeconds.Value) : sdkDefaults.Delay,
                MaxDelay = maximumBackoffInSeconds.HasValue ? TimeSpan.FromSeconds(maximumBackoffInSeconds.Value) : sdkDefaults.MaxDelay
            };
        }

        private void BindRetryPolicy(ServiceBusOptions serviceBusConfig)
        {
            var retryPolicySection = _serviceBusOptionsSection.GetSection(_retryPolicySectionName);
            if (!retryPolicySection.Exists())
            {
                return;
            }

            var retryPolicy = new RetryPolicyConfiguration();
            retryPolicySection.Bind(retryPolicy);
            serviceBusConfig.RetryPolicy = retryPolicy;
        }

        // INVARIANT: the effective retry options are RESOLVED ONCE, from the first source that stated one —
        // the fluent setter, then the bound RetryPolicy section, then the Azure SDK default — and the
        // resolution happens AFTER the fluent override is in hand. A configured section the fluent call
        // overrides is therefore DISCARDED WITHOUT BEING VALIDATED, which is the point: rejecting a value
        // that is about to be thrown away blocked host start on a retry policy the host was never going to
        // use, contradicting this module's fluent-wins precedence. Resolution and validation happen at the
        // same point, so neither can run against a value the other discarded.
        // A null return means retry options were never sourced at all — no fluent call and no bound
        // service-bus section — leaving ServiceBusOptions.RetryOptions unset.
        private ServiceBusRetryOptions ResolveRetryOptions(bool serviceBusSectionWasBound, RetryPolicyConfiguration retryPolicy)
        {
            if (_retryOptions != null)
            {
                return _retryOptions;
            }

            if (!serviceBusSectionWasBound)
            {
                return null;
            }

            if (retryPolicy == null)
            {
                return new ServiceBusRetryOptions();
            }

            if (retryPolicy.NoRetry)
            {
                return new ServiceBusRetryOptions { MaxRetries = 0 };
            }

            return CreateConfiguredRetryOptions(retryPolicy);
        }

        // INVARIANT: every configured parameter binds FAITHFULLY. An ABSENT parameter is null and leaves the
        // SDK default for that parameter in place; a STATED one is carried through to the SDK's own setter,
        // which raises its own failure for a value it cannot run with rather than having that value silently
        // replaced by the same default. A stated MaximumRetryCount of 0 therefore yields MaxRetries 0 — the
        // faithful binding of one explicitly written key. That differs from the earlier behaviour, which
        // inferred "off" only from an ALL-ZERO section. NoRetry, handled by ResolveRetryOptions, remains the
        // intention-revealing way to switch retry off, and issue #423 owns the final call on whether a stated
        // zero should keep binding this way.
        private static ServiceBusRetryOptions CreateConfiguredRetryOptions(RetryPolicyConfiguration retryPolicy)
        {
            var sdkDefaults = new ServiceBusRetryOptions();
            return CreateExponentialRetryOptions(
                retryPolicy.MaximumRetryCount ?? sdkDefaults.MaxRetries,
                retryPolicy.MaximumBackoffInSeconds,
                retryPolicy.MinimumBackoffInSeconds);
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
            var serviceBusSectionWasBound = _azureServiceBusSectionName != null && _serviceBusOptionsSection.Exists();
            if (serviceBusSectionWasBound)
            {
                // INVARIANT: bind INTO the default-initialized instance and never replace it; keys the
                // section omits keep their default. The bind surface is deliberately NARROW — plain Bind()
                // with BindNonPublicProperties OFF — and closure of the internal-set RetryOptions and
                // TokenCredential properties to configuration rests on BOTH that narrowness AND their NULL
                // default: the binder passes over a setter it cannot reach only while the value it reads
                // through the PUBLIC getter is null. Give either property a non-null initializer — say
                // = new ServiceBusRetryOptions() — and the binder would bind INTO that object instead,
                // driving its own PUBLIC setters, so a stray RetryOptions key would reach the SDK's
                // VALIDATING setter (MaxRetries rejects anything outside 0..100). The null default is
                // therefore LOAD-BEARING rather than incidental. Widening the surface would additionally let
                // a nested TokenCredential object drive the binder into activating an abstract type. Both
                // failures raise raw binder exceptions at host start, before any later assignment could
                // correct them. RetryPolicy is the ONE internal configuration property that must bind, so it
                // is bound EXPLICITLY below into a locally constructed instance whose own setters are public.
                // Binding is all that happens here: the configured RetryPolicy is carried forward as DATA
                // and only turned into retry options once, in the resolve step at the end of this method,
                // where the fluent override that may discard it is already in hand.
                _serviceBusOptionsSection.Bind(options);
                BindRetryPolicy(options);
            }

            if (string.IsNullOrWhiteSpace(_connectionString) && string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new Exception("A connection string is required.");
            }

            if (!string.IsNullOrWhiteSpace(_connectionString))
            {
                options.ConnectionString = _connectionString;
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

            // Resolve the effective retry options LAST among the option values, once every source is in
            // hand, so validation runs on the values this instance will actually carry and never on a
            // configured section the fluent override discarded.
            options.RetryOptions = ResolveRetryOptions(serviceBusSectionWasBound, options.RetryPolicy);

            // INVARIANT: every single-instance resolution of ServiceBusOptions returns the instance built here -
            // AddBuiltOptions registers it as the concrete type and as IOptions, IOptionsSnapshot and IOptionsMonitor
            // over that same instance - and it runs LAST, after the connection-string guard, the fluent-sentinel
            // overrides and the guarded retry construction, so no facet can observe a half-built instance. This
            // builder never registered a Configure<ServiceBusOptions>, so there is no second instance here to remove:
            // what the facets resolved instead was a framework-created all-default ServiceBusOptions whose
            // ConnectionString is null. Registering the facets COMPLETES the one-instance-everywhere invariant across
            // the options graph rather than repairing a divergence this builder introduced. The concrete registration is
            // APPENDED rather than replaced, so a second Build() on the same IServiceCollection takes over
            // single-instance resolution and leaves the earlier instances reachable through
            // IEnumerable<ServiceBusOptions> - each seeded and connection-string-guarded by its own Build(), so no
            // enumeration can surface a half-built instance.
            Services.AddBuiltOptions(options);

            return options;
        }
    }
}
