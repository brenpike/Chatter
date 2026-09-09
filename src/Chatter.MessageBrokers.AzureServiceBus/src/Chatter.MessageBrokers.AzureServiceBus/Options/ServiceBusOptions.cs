using Azure.Core;
using Azure.Messaging.ServiceBus;
using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Chatter.MessageBrokers.AzureServiceBus.Options
{
    /// <summary>
    /// A class containing various Azure Service Bus configuration values.
    /// </summary>
    public class ServiceBusOptions
    {
        [Required(AllowEmptyStrings = false, ErrorMessage = "A service bus connection string is required.")]
        public string ConnectionString { get; set; }
        public int MaxConcurrentCalls { get; set; } = 1;
        public int PrefetchCount { get; set; } = 0;
        // INVARIANT: cross-entity transactions are OFF by default. Enabling them pins the shared
        // ServiceBusClient to the first top-level entity it touches, so a second receiver on a different
        // top-level entity throws "Local transactions cannot span multiple top-level entities". This is
        // auto-enabled when a FullAtomicityViaInfrastructure receiver is registered; opt in explicitly here
        // only when a single-entity host needs cross-entity send+settle atomicity.
        public bool EnableCrossEntityTransactions { get; set; }
        /// <summary>
        /// How long a held session may yield no message before it is released and the receiver rolls
        /// to the next session. Applies only to session-enabled receivers.
        /// </summary>
        public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>
        /// The ceiling on how long a held session's lock is renewed for long-running processing.
        /// Once reached, renewal stops and the session is allowed to expire or roll naturally.
        /// Applies only to session-enabled receivers.
        /// </summary>
        public TimeSpan MaxSessionLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);
        internal RetryPolicyConfiguration RetryPolicy { get; set; }
        [JsonIgnore]
        public ServiceBusRetryOptions RetryOptions { get; internal set; }
        // INVARIANT: a null TokenCredential means "authenticate using the connection string's SAS".
        [JsonIgnore]
        public TokenCredential TokenCredential { get; internal set; } = null;
    }

    internal class RetryPolicyConfiguration
    {
        // INVARIANT: this is the INTENTION-REVEALING way to switch retry off — it says so outright instead
        // of leaving a reader to infer it from a count. It is no longer the only way: a stated
        // MaximumRetryCount of 0 now binds faithfully and yields MaxRetries 0, which differs both from the
        // earlier behaviour that inferred "off" only from an ALL-ZERO four-key section and from the
        // build-time validation that briefly refused a stated zero outright. Issue #423 owns the final call
        // on whether a stated zero should keep binding this way.
        public bool NoRetry { get; set; } = false;
        // INVARIANT: every numeric parameter is NULLABLE so that ABSENT and STATED are distinguishable and
        // each binds FAITHFULLY. Null means the key was never written, so the Azure SDK default for that
        // parameter stands; any other value was STATED by an operator and reaches the SDK's own setter,
        // which raises its own failure for a value it cannot run with. A non-nullable numeric could not
        // tell a stated zero from an absent key, which is why the two outcomes were previously fused into
        // one "greater than zero means configured" test that silently replaced a stated -5 with that same
        // SDK default.
        public double? MinimumBackoffInSeconds { get; set; }
        public double? MaximumBackoffInSeconds { get; set; }
        public int? MaximumRetryCount { get; set; }
        // INVARIANT: DeltaBackoffInSeconds is retained for configuration compatibility and IGNORED —
        // Azure.Messaging.ServiceBus has no per-attempt delta-backoff knob, exactly as
        // ServiceBusOptionsBuilder.WithExponentialDelay ignores its deltaBackoffInSeconds argument.
        public double? DeltaBackoffInSeconds { get; set; }
    }
}
