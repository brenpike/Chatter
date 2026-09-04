using Azure.Core;
using Azure.Messaging.ServiceBus;
using Chatter.MessageBrokers.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

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
        // The inclusive range ServiceBusRetryOptions.MaxRetries accepts inside its own setter.
        private const int _minimumSdkRetryCount = 0;
        private const int _maximumSdkRetryCount = 100;
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
        // WithExponentialDelay setter and the configuration-derived PostConfiguration both route through it,
        // so neither can hand a raw value to a validating SDK setter. ServiceBusRetryOptions.MaxRetries
        // validates its 0..100 range inside the setter and TimeSpan.FromSeconds overflows on a non-finite or
        // out-of-range number; both raise a bare exception naming only the SDK member, never the knob the
        // operator supplied. Every value is therefore checked BEFORE any SDK setter runs and every offending
        // knob is named in ONE failure, so an operator who corrected one value and redeployed does not then
        // discover the next.
        // A null backoff means "not configured", leaving the SDK default for that parameter in place.
        private static ServiceBusRetryOptions CreateExponentialRetryOptions(int maximumRetryCount, double? maximumBackoffInSeconds, double? minimumBackoffInSeconds)
        {
            var violations = new List<string>();
            if (maximumRetryCount < _minimumSdkRetryCount || maximumRetryCount > _maximumSdkRetryCount)
            {
                violations.Add($"'{nameof(RetryPolicyConfiguration.MaximumRetryCount)}' is {maximumRetryCount}, but the Azure Service Bus SDK accepts only {_minimumSdkRetryCount} through {_maximumSdkRetryCount}");
            }

            AddViolationWhenBackoffCannotBecomeATimeSpan(violations, nameof(RetryPolicyConfiguration.MaximumBackoffInSeconds), maximumBackoffInSeconds);
            AddViolationWhenBackoffCannotBecomeATimeSpan(violations, nameof(RetryPolicyConfiguration.MinimumBackoffInSeconds), minimumBackoffInSeconds);

            if (violations.Count > 0)
            {
                throw new ServiceBusRetryOptionsValidationException(violations);
            }

            var sdkDefaults = new ServiceBusRetryOptions();
            return new ServiceBusRetryOptions
            {
                Mode = ServiceBusRetryMode.Exponential,
                MaxRetries = maximumRetryCount,
                Delay = minimumBackoffInSeconds.HasValue ? TimeSpan.FromSeconds(minimumBackoffInSeconds.Value) : sdkDefaults.Delay,
                MaxDelay = maximumBackoffInSeconds.HasValue ? TimeSpan.FromSeconds(maximumBackoffInSeconds.Value) : sdkDefaults.MaxDelay
            };
        }

        // INVARIANT: a POSITIVE range check, not an enumeration of the bad values. Every number
        // TimeSpan.FromSeconds cannot convert — NaN, either infinity, a negative, and anything beyond
        // TimeSpan's own range — falls outside [0, TimeSpan.MaxValue.TotalSeconds] because no comparison
        // against NaN succeeds, so a new unconvertible value cannot slip past by not being listed. The
        // ceiling is derived from TimeSpan itself rather than written as a literal.
        private static void AddViolationWhenBackoffCannotBecomeATimeSpan(ICollection<string> violations, string knobName, double? backoffInSeconds)
        {
            if (!backoffInSeconds.HasValue)
            {
                return;
            }

            var seconds = backoffInSeconds.Value;
            if (seconds >= 0 && seconds <= TimeSpan.MaxValue.TotalSeconds)
            {
                return;
            }

            violations.Add($"'{knobName}' is {seconds}, but a backoff must be a number of seconds from 0 through {TimeSpan.MaxValue.TotalSeconds}");
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

        private void PostConfiguration(ServiceBusOptions serviceBusConfig)
        {
            if (serviceBusConfig == null)
            {
                return;
            }

            var retryPolicy = serviceBusConfig.RetryPolicy;
            if (retryPolicy == null)
            {
                serviceBusConfig.RetryOptions = new ServiceBusRetryOptions();
                return;
            }

            if (retryPolicy.NoRetry)
            {
                serviceBusConfig.RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0 };
                return;
            }

            // INVARIANT: disabling retry from configuration is UNREPRESENTABLE without the NoRetry opt-in
            // handled above. Each parameter falls back to the SDK default unless a value greater than zero
            // was configured, so no combination of configured values can derive MaxRetries = 0 — an
            // all-zero section yields the SDK default retry options rather than silently disabling retry.
            // That same fall-through is why a NaN or negative CONFIGURED number never reaches validation: it
            // is not greater than zero, so it reads as "not configured" and the SDK default stands. The
            // fluent path is deliberately asymmetric — a caller who states a negative outright stated a
            // value, and CreateExponentialRetryOptions reports it as a violation.
            var sdkDefaults = new ServiceBusRetryOptions();
            serviceBusConfig.RetryOptions = CreateExponentialRetryOptions(
                retryPolicy.MaximumRetryCount > 0 ? retryPolicy.MaximumRetryCount : sdkDefaults.MaxRetries,
                retryPolicy.MaximumBackoffInSeconds > 0 ? retryPolicy.MaximumBackoffInSeconds : (double?)null,
                retryPolicy.MinimumBackoffInSeconds > 0 ? retryPolicy.MinimumBackoffInSeconds : (double?)null);
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
                _serviceBusOptionsSection.Bind(options);
                BindRetryPolicy(options);
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
