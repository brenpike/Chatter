using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;

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

        /// <summary>
        /// Enables routing of messages to an outbox, rather than directly to messaging infrastructure. Using <see cref="OutboxProcessingBehavior{TMessage}"/>
        /// automatically enables outbox routing.
        /// </summary>
        /// <returns><see cref="ReliabilityOptionsBuilder"/></returns>
        public ReliabilityOptionsBuilder WithOutboxRouting()
        {
            _routeMessagesToOutbox = true;
            return this;
        }

        /// <summary>
        /// Defines how long outbox messages will live within the <see cref="InMemoryBrokeredMessageOutbox"/>. The <see cref="InMemoryBrokeredMessageOutbox"/>
        /// is registered by Chatter by default if no other persistance strategy is used. Default value is 10.
        /// </summary>
        /// <param name="timeToLiveInMinutes">The time messages will be maintained within the <see cref="InMemoryBrokeredMessageOutbox"/> before being purged.</param>
        /// <returns><see cref="ReliabilityOptionsBuilder"/></returns>
        public ReliabilityOptionsBuilder WithInMemoryOutboxTimeToLive(double timeToLiveInMinutes)
        {
            _minutesToLiveInMemory = timeToLiveInMinutes;
            return this;
        }

        /// <summary>
        /// Enables the <see cref="BrokeredMessageOutboxProcessor"/> which processes messages from the outbox and sends them to messaging infrastructure 
        /// at a timed interval. The default polling interval is 5000 milliseconds. This does not enable sending of messages to the outbox by default which
        /// must be done by calling <see cref="WithOutboxRouting"/> or by using <see cref="OutboxProcessingBehavior{TMessage}"/>.
        /// </summary>
        /// <param name="outboxProcessingIntervalInMilliseconds">The interval to wait before the outbox is checked for brokered messages</param>
        /// <returns><see cref="ReliabilityOptionsBuilder"/></returns>
        public ReliabilityOptionsBuilder WithOutboxPollingProcessor(int outboxProcessingIntervalInMilliseconds = 5000)
        {
            _enableOutboxPollingProcessor = true;
            _outboxProcessingIntervalInMilliseconds = outboxProcessingIntervalInMilliseconds;
            return this;
        }

        /// <summary>
        /// Produces the finalized <see cref="ReliabilityOptions"/> without touching the
        /// <see cref="IServiceCollection"/>.
        /// </summary>
        /// <returns>The finalized <see cref="ReliabilityOptions"/></returns>
        /// <remarks>
        /// INVARIANT: this is the COMPOSITION path and it publishes nothing. A parent builder seeds its own graph
        /// from here, binds its section over that graph and only then publishes, so no consumer can resolve an
        /// options instance the parent has not finished binding.
        /// </remarks>
        internal ReliabilityOptions Resolve()
        {
            var reliabilityOptions = new ReliabilityOptions();
            reliabilityOptions.RouteMessagesToOutbox = _routeMessagesToOutbox;
            reliabilityOptions.MinutesToLiveInMemory = _minutesToLiveInMemory;
            reliabilityOptions.EnableOutboxPollingProcessor = _enableOutboxPollingProcessor;
            reliabilityOptions.OutboxProcessingIntervalInMilliseconds = _outboxProcessingIntervalInMilliseconds;

            if (_reliabilityOptionsSection != null && _reliabilityOptionsSection.Exists())
            {
                // INVARIANT: bind INTO the fluent-defaulted instance and never replace it. Every property on
                // ReliabilityOptions is internal set, so the binder skips all of them unless BindNonPublicProperties is
                // on; replacing the instance would additionally discard the defaults assigned above. Keys the section
                // omits therefore keep their fluent default.
                _reliabilityOptionsSection.Bind(reliabilityOptions, o => o.BindNonPublicProperties = true);
            }

            return reliabilityOptions;
        }

        /// <summary>
        /// Registers the supplied finalized <see cref="ReliabilityOptions"/> against the
        /// <see cref="IServiceCollection"/>.
        /// </summary>
        /// <param name="reliabilityOptions">The finalized options produced by <see cref="Resolve"/></param>
        /// <remarks>
        /// INVARIANT: this is the ONLY site in this builder that touches the <see cref="IServiceCollection"/>. A
        /// parent builder calls it after its own bind so the registered instance is the finalized one; a standalone
        /// <see cref="Build"/> calls it immediately because there is no parent left to bind.
        /// </remarks>
        internal void Publish(ReliabilityOptions reliabilityOptions)
        {
            // INVARIANT: every single-instance resolution of ReliabilityOptions returns the instance published here -
            // AddBuiltOptions registers it as the concrete type and as IOptions, IOptionsSnapshot and
            // IOptionsMonitor over that same instance. The container's options factory is deliberately NOT used:
            // a Configure<ReliabilityOptions>(section) registration would build a second instance that never saw
            // the fluent defaults, so an application that resolved IOptions<ReliabilityOptions> - or the
            // snapshot or monitor form - for itself would have read OutboxProcessingIntervalInMilliseconds as
            // 0 where the resolved instance holds 5000. BrokeredMessageOutboxProcessor injects the concrete
            // ReliabilityOptions, so that divergent facet instance was never able to reach its polling
            // interval. The concrete registration is APPENDED rather than replaced, so a second Build() on the
            // same IServiceCollection takes over single-instance resolution and leaves the earlier instances
            // reachable through IEnumerable<ReliabilityOptions> - each seeded by its own Resolve(), so no
            // enumeration can surface an unseeded object.
            _services.AddBuiltOptions(reliabilityOptions);
        }

        public ReliabilityOptions Build()
        {
            var reliabilityOptions = Resolve();
            Publish(reliabilityOptions);
            return reliabilityOptions;
        }
    }
}
