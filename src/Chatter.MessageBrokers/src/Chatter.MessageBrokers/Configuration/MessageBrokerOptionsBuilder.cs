using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Reliability.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.Configuration
{
    public class MessageBrokerOptionsBuilder
    {
        public IServiceCollection Services { get; }
        private readonly IConfiguration _configuration;
        private TransactionMode _transactionMode = TransactionMode.ReceiveOnly;
        private ReliabilityOptionsBuilder _reliabilityOptionsBuilder = null;
        private RecoveryOptionsBuilder _recoveryOptionsBuilder = null;
        private IConfigurationSection _messageBrokerOptionsSection = null;

        public const string MessageBrokerSectionName = "Chatter:MessageBrokers";

        public static MessageBrokerOptionsBuilder Create(IServiceCollection services)
            => new MessageBrokerOptionsBuilder(services);

        private MessageBrokerOptionsBuilder(IServiceCollection services) : this(services, null, null) { }
        internal MessageBrokerOptionsBuilder(IServiceCollection services, IConfiguration configuration, IConfigurationSection section = null)
        {
            Services = services;
            _configuration = configuration;
            // INVARIANT: an explicitly supplied section wins; otherwise the documented default section is resolved
            // here. AddMessageBrokerOptions hands over an IConfiguration but no section, so without this resolution
            // Build() would have nothing to bind and every Chatter:MessageBrokers key would be discarded on the one
            // entry point consumers actually use.
            _messageBrokerOptionsSection = section ?? configuration?.GetSection(MessageBrokerSectionName);
        }

        public MessageBrokerOptionsBuilder WithTransactionMode(TransactionMode transactionMode)
        {
            _transactionMode = transactionMode;
            return this;
        }

        public MessageBrokerOptions FromConfig(string messageBrokerSectionName = MessageBrokerSectionName)
        {
            // INVARIANT: retarget THIS builder rather than delegating to the static overload. A throwaway builder
            // would discard the fluent state already accumulated here and would register a second, shadow set of
            // MessageBrokerOptions, ReliabilityOptions, RecoveryOptions and CircuitBreakerOptions singletons.
            _messageBrokerOptionsSection = _configuration?.GetSection(messageBrokerSectionName);
            return Build();
        }

        public static MessageBrokerOptions FromConfig(IServiceCollection services, IConfiguration configuration, string messageBrokerSectionName = MessageBrokerSectionName)
        {
            var section = configuration?.GetSection(messageBrokerSectionName);
            var builder = new MessageBrokerOptionsBuilder(services, configuration, section);
            return builder.Build();
        }

        public MessageBrokerOptionsBuilder AddReliabilityOptions(Action<ReliabilityOptionsBuilder> builder)
        {
            // INVARIANT: the ONE retained sub-builder is configured in place rather than replaced by a fresh one, so
            // a second call accumulates onto the first call's state instead of discarding it. It is also never built
            // here: building it would register ReliabilityOptions before this builder has bound its own section, so a
            // consumer could resolve an instance the parent bind had not been applied to yet.
            var reliabilityOptionsBuilder = EnsureReliabilityOptionsBuilder();
            builder?.Invoke(reliabilityOptionsBuilder);
            return this;
        }

        public MessageBrokerOptionsBuilder AddRecoveryOptions(Action<RecoveryOptionsBuilder> builder)
        {
            // INVARIANT: the ONE retained sub-builder is configured in place rather than replaced by a fresh one, so
            // a second call accumulates onto the first call's state instead of discarding it - the retry and circuit
            // breaker exception predicates are consumed as an enumeration, so a discarded builder's predicates would
            // never be registered at all. It is also never built here: building it would register RecoveryOptions and
            // CircuitBreakerOptions before this builder has bound its own section, so a consumer could resolve
            // instances the parent bind had not been applied to yet.
            var recoveryOptionsBuilder = EnsureRecoveryOptionsBuilder();
            builder?.Invoke(recoveryOptionsBuilder);
            return this;
        }

        /// <summary>
        /// Produces the finalized <see cref="MessageBrokerOptions"/>, nested options included, without touching the
        /// <see cref="IServiceCollection"/>.
        /// </summary>
        /// <returns>The finalized <see cref="MessageBrokerOptions"/></returns>
        /// <remarks>
        /// INVARIANT: this is the COMPOSITION path and it publishes nothing, so nothing can be resolved from the
        /// container before this builder has bound its own section over the whole graph.
        /// </remarks>
        internal MessageBrokerOptions Resolve()
        {
            var messageBrokerOptions = new MessageBrokerOptions();
            messageBrokerOptions.TransactionMode = _transactionMode;
            // INVARIANT: the nested Reliability and Recovery options are seeded BEFORE the parent bind so the binder
            // mutates the instances their sub-builders resolved instead of replacing them. InMemoryBrokeredMessageOutbox,
            // BrokeredMessageOutboxProcessor, RetryStrategy, RetryWithCircuitBreakerStrategy and CircuitBreaker all
            // inject the concrete options types, and Build publishes the instances reachable from the finalized graph,
            // so no consumer can reach an orphaned one.
            messageBrokerOptions.Reliability = EnsureReliabilityOptionsBuilder().Resolve();
            messageBrokerOptions.Recovery = EnsureRecoveryOptionsBuilder().Resolve();

            if (_messageBrokerOptionsSection != null && _messageBrokerOptionsSection.Exists())
            {
                // INVARIANT: bind INTO the fluent-defaulted instance and never replace it. Every property on
                // MessageBrokerOptions is internal set, so the binder skips all of them unless BindNonPublicProperties
                // is on; replacing the instance would additionally discard the defaults assigned above - which is how
                // TransactionMode degraded from ReceiveOnly to None. Keys the section omits keep their fluent default.
                _messageBrokerOptionsSection.Bind(messageBrokerOptions, o => o.BindNonPublicProperties = true);
            }

            return messageBrokerOptions;
        }

        /// <summary>
        /// Refuses any value on the finalized <see cref="MessageBrokerOptions"/>, nested options included.
        /// </summary>
        /// <param name="messageBrokerOptions">The finalized options produced by <see cref="Resolve"/></param>
        /// <exception cref="ConfiguredValueRefusedException">A configured value was refused</exception>
        /// <remarks>
        /// INVARIANT: validation is its own phase between <see cref="Resolve"/> and the publish step, and it walks
        /// the finalized graph. It cannot live in Resolve, because the nested builders have no section of their own
        /// and every value configured for them arrives from the bind above AFTER their Resolve has returned; and it
        /// cannot live in the publish step, because that publishes Reliability before Recovery, so a refusal raised
        /// there would leave the reliability options registered. Recursing into the same sub-builders the
        /// composition path visits keeps compose, validate and publish on one graph.
        /// </remarks>
        internal void Validate(MessageBrokerOptions messageBrokerOptions)
        {
            // INVARIANT: the configuration binder is loud only for a transaction mode it cannot PARSE. A numeric
            // literal converts cleanly to an undefined member of this byte-backed enum and reaches the
            // TransactionContext the receiver builds, so the enum type itself is the oracle for what may be
            // configured rather than a set restated here.
            if (!Enum.IsDefined(typeof(TransactionMode), messageBrokerOptions.TransactionMode))
            {
                throw new ConfiguredValueRefusedException($"{nameof(MessageBrokerOptions)}.{nameof(MessageBrokerOptions.TransactionMode)}",
                                                          messageBrokerOptions.TransactionMode,
                                                          $"one of {string.Join(", ", Enum.GetNames(typeof(TransactionMode)))}",
                                                          _messageBrokerOptionsSection?.Path);
            }

            // INVARIANT: a null child is left to the publish step, which fails host registration loudly on it,
            // rather than dereferenced here - validation must not turn a registration failure into a
            // NullReferenceException.
            if (messageBrokerOptions.Reliability != null)
            {
                EnsureReliabilityOptionsBuilder().Validate(messageBrokerOptions.Reliability);
            }

            if (messageBrokerOptions.Recovery != null)
            {
                EnsureRecoveryOptionsBuilder().Validate(messageBrokerOptions.Recovery);
            }
        }

        internal MessageBrokerOptions Build()
        {
            var messageBrokerOptions = Resolve();

            Validate(messageBrokerOptions);

            // INVARIANT: this is the ONE publish site for the whole options graph and it runs only after the section
            // above has been bound. The nested options are published from the finalized graph rather than from the
            // sub-builders' own return values, so every entry point - the concrete type, the options facets and
            // MessageBrokerOptions.Reliability / .Recovery / .Recovery.CircuitBreakerOptions - reaches one instance
            // per options type.
            EnsureReliabilityOptionsBuilder().Publish(messageBrokerOptions.Reliability);
            EnsureRecoveryOptionsBuilder().Publish(messageBrokerOptions.Recovery);

            // INVARIANT: every single-instance resolution of MessageBrokerOptions returns the instance built here -
            // AddBuiltOptions registers it as the concrete type and as IOptions, IOptionsSnapshot and IOptionsMonitor
            // over that same instance. Unlike the nested builders this one never registered a
            // Configure<MessageBrokerOptions>, so there is no second instance here to remove: what the facets resolved
            // instead was a framework-created all-default MessageBrokerOptions, whose TransactionMode is None and
            // whose Reliability and Recovery are null. Registering the facets completes the one-instance-everywhere
            // invariant across the whole options graph rather than repairing a divergence this builder introduced. The
            // concrete registration is APPENDED rather than replaced, so a second Build() on the same
            // IServiceCollection takes over single-instance resolution and leaves the earlier instances reachable
            // through IEnumerable<MessageBrokerOptions> - each seeded by its own Build(), so no enumeration can
            // surface an unseeded object.
            Services.AddBuiltOptions(messageBrokerOptions);

            return messageBrokerOptions;
        }

        // INVARIANT: Resolve and Build must reach the SAME sub-builder, so the default one is created once and
        // retained here rather than constructed at each call site.
        private ReliabilityOptionsBuilder EnsureReliabilityOptionsBuilder()
        {
            if (_reliabilityOptionsBuilder == null)
            {
                _reliabilityOptionsBuilder = ReliabilityOptionsBuilder.Create(Services);
            }

            return _reliabilityOptionsBuilder;
        }

        // INVARIANT: Resolve and Build must reach the SAME sub-builder, so the default one is created once and
        // retained here rather than constructed at each call site.
        private RecoveryOptionsBuilder EnsureRecoveryOptionsBuilder()
        {
            if (_recoveryOptionsBuilder == null)
            {
                _recoveryOptionsBuilder = RecoveryOptionsBuilder.Create(Services);
            }

            return _recoveryOptionsBuilder;
        }
    }
}
