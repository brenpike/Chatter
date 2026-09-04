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
        private ReliabilityOptions _reliabilityOptions = null;
        private RecoveryOptions _recoveryOptions = null;
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
            var b = ReliabilityOptionsBuilder.Create(Services);
            builder?.Invoke(b);
            _reliabilityOptions = b.Build();
            return this;
        }

        public MessageBrokerOptionsBuilder AddRecoveryOptions(Action<RecoveryOptionsBuilder> builder)
        {
            var b = RecoveryOptionsBuilder.Create(Services);
            builder?.Invoke(b);
            _recoveryOptions = b.Build();
            return this;
        }

        internal MessageBrokerOptions Build()
        {
            var messageBrokerOptions = new MessageBrokerOptions();
            messageBrokerOptions.TransactionMode = _transactionMode;
            // INVARIANT: the nested Reliability and Recovery options are seeded BEFORE the parent bind so the binder
            // mutates the instances their sub-builders already registered as singletons instead of replacing them with
            // unregistered ones. InMemoryBrokeredMessageOutbox, BrokeredMessageOutboxProcessor, RetryStrategy,
            // RetryWithCircuitBreakerStrategy and CircuitBreaker all inject the concrete options types, so an orphaned
            // instance would surface as a resolution failure.
            messageBrokerOptions.Reliability = _reliabilityOptions ?? ReliabilityOptionsBuilder.Create(Services).Build();
            messageBrokerOptions.Recovery = _recoveryOptions ?? RecoveryOptionsBuilder.Create(Services).Build();

            if (_messageBrokerOptionsSection != null && _messageBrokerOptionsSection.Exists())
            {
                // INVARIANT: bind INTO the fluent-defaulted instance and never replace it. Every property on
                // MessageBrokerOptions is internal set, so the binder skips all of them unless BindNonPublicProperties
                // is on; replacing the instance would additionally discard the defaults assigned above - which is how
                // TransactionMode degraded from ReceiveOnly to None. Keys the section omits keep their fluent default.
                _messageBrokerOptionsSection.Bind(messageBrokerOptions, o => o.BindNonPublicProperties = true);
            }

            // INVARIANT: circuit breaker options reached through the MessageBrokers parent section never pass through
            // CircuitBreakerOptionsBuilder.Build() or RecoveryOptionsBuilder.Build(), both of which run before the bind
            // above mutates them, so validating the finalized instance here is what makes build-time validation
            // reachable from this entry point too.
            messageBrokerOptions.Recovery.CircuitBreakerOptions.Validate();

            Services.AddSingleton(messageBrokerOptions);

            return messageBrokerOptions;
        }
    }
}
