using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;

namespace Chatter.MessageBrokers.Reliability.Configuration
{
    public class ReliabilityOptionsBuilder
    {
        private bool _routeMessagesToOutbox = false;
        private double _minutesToLiveInMemory = 10;
        private bool _enableOutboxPollingProcessor = false;
        private int _outboxProcessingIntervalInMilliseconds = 5000;
        private int _inMemoryInboxDeduplicationWindowInMinutes = 60;
        private int _inMemoryInboxMaxEntries = 200000;
        private int _outboxPollBatchSize = 100;
        private int _outboxDispatchBackoffBaseInSeconds = 5;
        private int _outboxDispatchBackoffCapInSeconds = 60;
        private int? _outboxMaxDispatchAttempts = null;

        private const int _minimumOutboxProcessingIntervalInMilliseconds = 0;
        private const string _outboxProcessingIntervalBound = "at least 0 milliseconds";
        private const string _minutesToLiveInMemoryBound = "at most 0 minutes, which disables expiry cleanup, or a finite number of minutes";
        private const int _minimumInMemoryInboxMaxEntries = 1;
        private const string _inMemoryInboxMaxEntriesBound = "at least 1 entry";
        private const int _minimumInMemoryInboxDeduplicationWindowInMinutes = 1;
        private const string _inMemoryInboxDeduplicationWindowBound = "at least 1 minute";
        private const int _minimumOutboxPollBatchSize = 1;
        private const string _outboxPollBatchSizeBound = "at least 1 message";
        private const int _minimumOutboxDispatchBackoffInSeconds = 1;
        private const string _outboxDispatchBackoffBound = "at least 1 second";
        private const int _minimumOutboxMaxDispatchAttempts = 1;
        private const string _outboxMaxDispatchAttemptsBound = "at least 1 attempt, or absent to re-attempt for good";

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
        /// Defines the deduplication window of the <see cref="InMemoryBrokeredMessageInbox"/>: after a receipt completes,
        /// a redelivery of the same message id is skipped for this long. The <see cref="InMemoryBrokeredMessageInbox"/>
        /// is registered by Chatter by default if no other persistance strategy is used. Default value is 60.
        /// </summary>
        /// <param name="windowInMinutes">The time a completed receipt deduplicates redeliveries of its message id for, and
        /// the lease on an in-flight reservation. Must be at least 1 minute. A handler that can run longer than this window
        /// is pre-empted by a second handler for the same message id, so size the window above the slowest handler and keep
        /// that handler idempotent.</param>
        /// <returns><see cref="ReliabilityOptionsBuilder"/></returns>
        public ReliabilityOptionsBuilder WithInMemoryInboxDeduplicationWindow(int windowInMinutes)
        {
            _inMemoryInboxDeduplicationWindowInMinutes = windowInMinutes;
            return this;
        }

        /// <summary>
        /// Defines the most receipts the <see cref="InMemoryBrokeredMessageInbox"/> will retain. This is a memory safety
        /// valve rather than the deduplication guarantee: under memory pressure it truncates the window advertised by
        /// <see cref="WithInMemoryInboxDeduplicationWindow"/>, so a redelivery within that window can be handled again
        /// once its receipt has been evicted. Default value is 200000.
        /// </summary>
        /// <param name="maxEntries">The most receipts to retain. Must be at least 1.</param>
        /// <returns><see cref="ReliabilityOptionsBuilder"/></returns>
        public ReliabilityOptionsBuilder WithInMemoryInboxMaxEntries(int maxEntries)
        {
            _inMemoryInboxMaxEntries = maxEntries;
            return this;
        }

        /// <summary>
        /// Defines the most outbox messages a single poll of the <see cref="BrokeredMessageOutboxProcessor"/> takes,
        /// so that poll cost is bounded by this number rather than by the number of unprocessed messages. Default
        /// value is 100.
        /// </summary>
        /// <param name="batchSize">The most messages to take per poll. Must be at least 1.</param>
        /// <returns><see cref="ReliabilityOptionsBuilder"/></returns>
        public ReliabilityOptionsBuilder WithOutboxPollBatchSize(int batchSize)
        {
            _outboxPollBatchSize = batchSize;
            return this;
        }

        /// <summary>
        /// Defines how long the <see cref="BrokeredMessageOutboxProcessor"/> waits before it attempts a message whose
        /// dispatch failed again. The wait starts at <paramref name="baseDelayInSeconds"/> after the first failure and
        /// doubles per further attempt until it reaches <paramref name="capInSeconds"/>. Default values are 5 and 60.
        /// The wait is derived from the attempt count the message carries, so it costs no schedule of its own.
        /// </summary>
        /// <param name="baseDelayInSeconds">The wait after the first failed attempt. Must be at least 1 second.</param>
        /// <param name="capInSeconds">The longest wait the doubling reaches. Must be at least 1 second. A cap below
        /// the base names a fixed wait of the cap.</param>
        /// <returns><see cref="ReliabilityOptionsBuilder"/></returns>
        public ReliabilityOptionsBuilder WithOutboxDispatchBackoff(int baseDelayInSeconds = 5, int capInSeconds = 60)
        {
            _outboxDispatchBackoffBaseInSeconds = baseDelayInSeconds;
            _outboxDispatchBackoffCapInSeconds = capInSeconds;
            return this;
        }

        /// <summary>
        /// Defines the most times dispatch of one outbox message may be attempted. Calling this is how the ceiling is
        /// turned ON: it is absent by default, and a message is otherwise re-attempted for good.
        /// </summary>
        /// <param name="maxDispatchAttempts">The most attempts one message may cost. Must be at least 1.</param>
        /// <returns><see cref="ReliabilityOptionsBuilder"/></returns>
        public ReliabilityOptionsBuilder WithOutboxMaxDispatchAttempts(int maxDispatchAttempts)
        {
            _outboxMaxDispatchAttempts = maxDispatchAttempts;
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
            reliabilityOptions.InMemoryInboxDeduplicationWindowInMinutes = _inMemoryInboxDeduplicationWindowInMinutes;
            reliabilityOptions.InMemoryInboxMaxEntries = _inMemoryInboxMaxEntries;
            reliabilityOptions.OutboxPollBatchSize = _outboxPollBatchSize;
            reliabilityOptions.OutboxDispatchBackoffBaseInSeconds = _outboxDispatchBackoffBaseInSeconds;
            reliabilityOptions.OutboxDispatchBackoffCapInSeconds = _outboxDispatchBackoffCapInSeconds;
            reliabilityOptions.OutboxMaxDispatchAttempts = _outboxMaxDispatchAttempts;

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
        /// Refuses any value on the finalized <see cref="ReliabilityOptions"/>.
        /// </summary>
        /// <param name="reliabilityOptions">The finalized options produced by <see cref="Resolve"/></param>
        /// <exception cref="ConfiguredValueRefusedException">A configured value was refused</exception>
        /// <remarks>
        /// INVARIANT: validation is its own phase between <see cref="Resolve"/> and <see cref="Publish"/>. It cannot
        /// live in Resolve, because this builder has no section of its own when a parent composes it and every
        /// configured value then arrives from the parent bind AFTER Resolve has returned; and it cannot live in
        /// Publish, because a parent publishes its other children around this one and a refusal raised there would
        /// leave them registered.
        /// </remarks>
        internal void Validate(ReliabilityOptions reliabilityOptions)
        {
            // INVARIANT: BrokeredMessageOutboxProcessor.ExecuteAsync awaits Task.Delay(interval) with no try of its
            // own, so anything below -1 faults the whole background service; -1 is Timeout.Infinite, which an ENABLED
            // poller would wait on for good. Zero stays accepted - Task.Delay completes it immediately, so how
            // aggressively the outbox is polled is the operator's call. That processor is the interval's only reader
            // and ChatterMessageBrokerExtensions registers it only when EnableOutboxPollingProcessor is set, so the
            // refusal is asked only of a host that will run one: with the poller off nothing ever waits on the value
            // and a host carrying a stale out-of-range one still starts.
            if (reliabilityOptions.EnableOutboxPollingProcessor
             && reliabilityOptions.OutboxProcessingIntervalInMilliseconds < _minimumOutboxProcessingIntervalInMilliseconds)
            {
                throw new ConfiguredValueRefusedException($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.OutboxProcessingIntervalInMilliseconds)}",
                                                          reliabilityOptions.OutboxProcessingIntervalInMilliseconds,
                                                          _outboxProcessingIntervalBound,
                                                          _reliabilityOptionsSection?.Path);
            }

            if (!CanScheduleExpiry(reliabilityOptions.MinutesToLiveInMemory))
            {
                throw new ConfiguredValueRefusedException($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.MinutesToLiveInMemory)}",
                                                          reliabilityOptions.MinutesToLiveInMemory,
                                                          _minutesToLiveInMemoryBound,
                                                          _reliabilityOptionsSection?.Path);
            }

            // INVARIANT: the cap is the in-memory inbox's memory safety valve, so it must name a number of receipts the
            // inbox can actually hold. A cap of zero would retain no receipt while the inbox still allocated for every
            // one of them - deduplicating nothing under a setting that reads like a retention limit - and a negative cap
            // names no number at all. Neither is read as 'disabled'.
            if (reliabilityOptions.InMemoryInboxMaxEntries < _minimumInMemoryInboxMaxEntries)
            {
                throw new ConfiguredValueRefusedException($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.InMemoryInboxMaxEntries)}",
                                                          reliabilityOptions.InMemoryInboxMaxEntries,
                                                          _inMemoryInboxMaxEntriesBound,
                                                          _reliabilityOptionsSection?.Path);
            }

            // INVARIANT: the window is the in-memory inbox's ONLY reclamation rule - every entry it holds, a completed
            // receipt and an in-flight reservation alike, is released by that rule and by nothing else - so it is
            // mandatory and positive. A non-positive window would leave no entry reclaimable at all: a hung handler's
            // reservation would never end, and the cap, which never takes a reservation the window still honours, would
            // then have nothing left it could evict, which turns the store's out-of-memory safety valve off along with
            // expiry. It is deliberately NOT read as 'disabled' the way a non-positive MinutesToLiveInMemory is: that
            // one governs cleanup of rows the outbox has already processed, so disabling it only retains them, while
            // disabling this one would let the inbox hold an entry forever with nothing able to remove it.
            if (reliabilityOptions.InMemoryInboxDeduplicationWindowInMinutes < _minimumInMemoryInboxDeduplicationWindowInMinutes)
            {
                throw new ConfiguredValueRefusedException($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.InMemoryInboxDeduplicationWindowInMinutes)}",
                                                          reliabilityOptions.InMemoryInboxDeduplicationWindowInMinutes,
                                                          _inMemoryInboxDeduplicationWindowBound,
                                                          _reliabilityOptionsSection?.Path);
            }

            // INVARIANT: the batch size bounds what a single poll takes, so it must name a number of messages the
            // poll can actually take. A batch of zero would take nothing on every pass, leaving the outbox undrained
            // for as long as the host ran, and a negative batch names no number at all. Neither is read as
            // 'disabled': EnableOutboxPollingProcessor is where an operator says the outbox is not to be polled.
            // Unlike the polling interval above, the refusal is asked of EVERY host rather than only one that will
            // run the poller: an interval the poller never waits on is inert, while a batch of zero is a setting that
            // reads like a limit and silently drains nothing the moment the poller is switched on.
            if (reliabilityOptions.OutboxPollBatchSize < _minimumOutboxPollBatchSize)
            {
                throw new ConfiguredValueRefusedException($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.OutboxPollBatchSize)}",
                                                          reliabilityOptions.OutboxPollBatchSize,
                                                          _outboxPollBatchSizeBound,
                                                          _reliabilityOptionsSection?.Path);
            }

            // INVARIANT: the backoff is what keeps a message whose dispatch keeps failing from holding its place at
            // the head of every poll batch, so neither end of it may name a wait of nothing. A base of zero re-attempts
            // such a message on every poll, and a cap of zero flattens every wait however many attempts the message has
            // already cost, because the wait is the smaller of the two. Neither is read as 'disabled': there is no
            // configuration under which the outbox re-attempts a failed message without waiting. The refusal is asked
            // of EVERY host, on the same terms as the batch size above. Oracles:
            // WhenBuilding.MustRefuseAConfiguredOutboxDispatchBackoffBaseOfZero and
            // WhenBuilding.MustRefuseAConfiguredOutboxDispatchBackoffCapOfZero; widening one comparison to '< 0'
            // reddens that end's zero fact alone and leaves the other end and both negative facts green - observed
            // for each end in turn.
            if (reliabilityOptions.OutboxDispatchBackoffBaseInSeconds < _minimumOutboxDispatchBackoffInSeconds)
            {
                throw new ConfiguredValueRefusedException($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.OutboxDispatchBackoffBaseInSeconds)}",
                                                          reliabilityOptions.OutboxDispatchBackoffBaseInSeconds,
                                                          _outboxDispatchBackoffBound,
                                                          _reliabilityOptionsSection?.Path);
            }

            if (reliabilityOptions.OutboxDispatchBackoffCapInSeconds < _minimumOutboxDispatchBackoffInSeconds)
            {
                throw new ConfiguredValueRefusedException($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.OutboxDispatchBackoffCapInSeconds)}",
                                                          reliabilityOptions.OutboxDispatchBackoffCapInSeconds,
                                                          _outboxDispatchBackoffBound,
                                                          _reliabilityOptionsSection?.Path);
            }

            // INVARIANT: an ABSENT ceiling is how an operator says a message is re-attempted for good, so absence is
            // accepted and only a stated value is bounded. A ceiling of zero would abandon every message before it was
            // ever dispatched, which is not what asking for a ceiling means, and a negative one names no number of
            // attempts at all. Oracle for the bound: WhenBuilding.MustRefuseAConfiguredOutboxMaxDispatchAttemptsOfZero,
            // reddened alone by widening the comparison to '< 0' (observed). Acceptance of absence is pinned by
            // WhenBuilding.MustAcceptAnOmittedOutboxMaxDispatchAttempts and WhenBuilding.MustBuildDefaultOptions, and
            // the mutation that reddens exactly those two is seeding _outboxMaxDispatchAttempts above with a value
            // (observed). Refusing absence HERE is not that mutation and is not named as one: every host that never
            // states a ceiling would then fail to build, which reddens most of this suite. Dropping the HasValue
            // guard on its own reddens NOTHING - a null compares false against the bound, so absence stays accepted.
            if (reliabilityOptions.OutboxMaxDispatchAttempts.HasValue
             && reliabilityOptions.OutboxMaxDispatchAttempts.Value < _minimumOutboxMaxDispatchAttempts)
            {
                throw new ConfiguredValueRefusedException($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.OutboxMaxDispatchAttempts)}",
                                                          reliabilityOptions.OutboxMaxDispatchAttempts.Value,
                                                          _outboxMaxDispatchAttemptsBound,
                                                          _reliabilityOptionsSection?.Path);
            }
        }

        private static bool CanScheduleExpiry(double minutesToLiveInMemory)
        {
            // INVARIANT: InMemoryBrokeredMessageOutbox opens its expiry scan with this very comparison and returns on
            // it before it reads a single timestamp, so a ttl the scan disables itself on is one it runs without
            // computing anything - accepted whatever its magnitude. The comparison is written the way the scan writes
            // it rather than negated: every comparison against a NaN is false, so a NaN falls through to the
            // finiteness question below instead of being waved through here as non-positive.
            if (minutesToLiveInMemory <= 0)
            {
                return true;
            }

            // INVARIANT: only a NaN or a positive infinity reaches here, and the scan's disable branch has already
            // declined to cover either. No magnitude is refused alongside them: the scan compares elapsed minutes
            // against the ttl rather than computing an expiry instant, so it runs every finite number without
            // faulting and there is no arithmetic left for this builder to derive a bound from. These two are
            // refused as intent that names no number of minutes - a NaN expires every message the instant it is
            // processed, and an infinity says nothing ever expires, which a non-positive ttl already states.
            return double.IsFinite(minutesToLiveInMemory);
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
            Validate(reliabilityOptions);
            Publish(reliabilityOptions);
            return reliabilityOptions;
        }
    }
}
