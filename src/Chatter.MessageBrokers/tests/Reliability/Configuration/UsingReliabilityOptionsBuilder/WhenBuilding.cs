using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Configuration.UsingReliabilityOptionsBuilder
{
    public class WhenBuilding : Testing.Core.Context
    {
        [Fact]
        public void MustBuildDefaultOptions()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).Build();

            options.RouteMessagesToOutbox.Should().BeFalse();
            options.MinutesToLiveInMemory.Should().Be(10);
            options.EnableOutboxPollingProcessor.Should().BeFalse();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
            options.InMemoryInboxDeduplicationWindowInMinutes.Should().Be(60);
            options.InMemoryInboxMaxEntries.Should().Be(200000);
        }

        [Fact]
        public void MustNotRegisterAnyServiceWhenResolved()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithOutboxRouting().Resolve();

            options.RouteMessagesToOutbox.Should().BeTrue();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
            services.Should().BeEmpty();
        }

        /// <summary>
        /// A configured value of the wrong TYPE is the one configuration failure that still happens while
        /// the options are being built, and the one that names the key: <c>ConfigurationBinder</c> cannot
        /// convert it, so it throws out of <c>Build()</c> before any runtime sink sees the value. Recorded
        /// here so the distinction between a conversion failure and the absent semantic validation issue
        /// #423 tracks is pinned rather than only described in prose. The message is asserted only for the
        /// KEY PATH — the framework words the rest differently on net8.0 and net10.0.
        /// </summary>
        [Fact]
        public void MustFailInTheBinderNamingTheKeyWhenAConfiguredValueIsNotConvertible()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:MinutesToLiveInMemory"] = "abc"
                })
                .Build();

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<InvalidOperationException>()
                      .Which.Message.Should().Contain($"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:MinutesToLiveInMemory");
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServicesIsNull()
        {
            var create = () => ReliabilityOptionsBuilder.Create(null);

            create.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustEnableOutboxRoutingWhenWithOutboxRoutingCalled()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithOutboxRouting().Build();

            options.RouteMessagesToOutbox.Should().BeTrue();
        }

        [Fact]
        public void MustReflectWithInMemoryOutboxTimeToLive()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithInMemoryOutboxTimeToLive(99).Build();

            options.MinutesToLiveInMemory.Should().Be(99);
        }

        [Fact]
        public void MustReflectWithInMemoryInboxDeduplicationWindow()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithInMemoryInboxDeduplicationWindow(15).Build();

            options.InMemoryInboxDeduplicationWindowInMinutes.Should().Be(15);
        }

        [Fact]
        public void MustReflectWithInMemoryInboxMaxEntries()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithInMemoryInboxMaxEntries(1500).Build();

            options.InMemoryInboxMaxEntries.Should().Be(1500);
        }

        /// <summary>
        /// Both in-memory inbox settings are seeded before the section is bound, so a configured value takes the
        /// fluent default's place rather than being overwritten by it.
        /// </summary>
        [Fact]
        public void MustHonourConfiguredInMemoryInboxSettingsOverTheirFluentDefaults()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:InMemoryInboxDeduplicationWindowInMinutes"] = "5",
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:InMemoryInboxMaxEntries"] = "25"
                })
                .Build();

            var options = ReliabilityOptionsBuilder.FromConfig(services, configuration);

            options.InMemoryInboxDeduplicationWindowInMinutes.Should().Be(5);
            options.InMemoryInboxMaxEntries.Should().Be(25);
        }

        [Fact]
        public void MustEnableOutboxPollingProcessorWithCustomIntervalWhenWithOutboxPollingProcessorCalled()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithOutboxPollingProcessor(7500).Build();

            options.EnableOutboxPollingProcessor.Should().BeTrue();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(7500);
        }

        [Fact]
        public void MustEnableOutboxPollingProcessorWithDefaultIntervalWhenWithOutboxPollingProcessorCalledWithoutArgument()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.Create(services).WithOutboxPollingProcessor().Build();

            options.EnableOutboxPollingProcessor.Should().BeTrue();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
        }

        [Fact]
        public void MustHonourConfiguredValuesAndRetainOmittedFluentDefaultsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:RouteMessagesToOutbox"] = "true",
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:OutboxProcessingIntervalInMilliseconds"] = "1234"
                })
                .Build();

            var options = ReliabilityOptionsBuilder.FromConfig(services, configuration);

            options.Should().NotBeNull();
            options.RouteMessagesToOutbox.Should().BeTrue();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(1234);
            options.MinutesToLiveInMemory.Should().Be(10);
            options.EnableOutboxPollingProcessor.Should().BeFalse();
        }

        [Fact]
        public void MustRetainEveryFluentDefaultWhenFromConfigSectionPresentWithoutChildKeys()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [ReliabilityOptionsBuilder.ReliabilityOptionsSectionName] = string.Empty
                })
                .Build();
            configuration.GetSection(ReliabilityOptionsBuilder.ReliabilityOptionsSectionName).Exists().Should().BeTrue();

            var options = ReliabilityOptionsBuilder.FromConfig(services, configuration);

            options.RouteMessagesToOutbox.Should().BeFalse();
            options.MinutesToLiveInMemory.Should().Be(10);
            options.EnableOutboxPollingProcessor.Should().BeFalse();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithRouteMessagesToOutbox());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<ReliabilityOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<ReliabilityOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsSnapshotWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithRouteMessagesToOutbox());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsSnapshot<ReliabilityOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<ReliabilityOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsMonitorWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithRouteMessagesToOutbox());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsMonitor<ReliabilityOptions>>().CurrentValue
                .Should().BeSameAs(provider.GetRequiredService<ReliabilityOptions>());
        }

        [Fact]
        public void MustRetainOmittedFluentDefaultsOnTheBuiltOptionsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithRouteMessagesToOutbox());

            using var provider = services.BuildServiceProvider();
            var builtOptions = provider.GetRequiredService<IOptions<ReliabilityOptions>>().Value;

            builtOptions.RouteMessagesToOutbox.Should().BeTrue();
            builtOptions.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
            builtOptions.MinutesToLiveInMemory.Should().Be(10);
        }

        /// <summary>
        /// <c>Task.Delay(0)</c> is legal and completes immediately, so how aggressively the outbox is polled is the
        /// operator's call.
        /// </summary>
        [Fact]
        public void MustAllowAZeroOutboxProcessingInterval()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("0", enableOutboxPollingProcessor: true);

            var options = ReliabilityOptionsBuilder.FromConfig(services, configuration);

            options.OutboxProcessingIntervalInMilliseconds.Should().Be(0);
        }

        /// <summary>
        /// <c>Task.Delay</c> rejects anything below -1, so an enabled outbox polling processor configured this way
        /// faults the whole background service. This builder used to bind the value and let the host start, and the
        /// acceptance was recorded here as a deferral naming issue #423. #423 closes it: the value is now refused at
        /// build time, and the deferral record becomes the pin for the refusal. The poller is stated here because
        /// the refusal is asked only of a host that will run one - see the two pins below.
        /// </summary>
        [Fact]
        public void MustRefuseAConfiguredOutboxProcessingIntervalOfNegativeFive()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("-5", enableOutboxPollingProcessor: true);

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<ConfiguredValueRefusedException>();
            services.Should().BeEmpty();
        }

        /// <summary>
        /// <c>BrokeredMessageOutboxProcessor</c> is the interval's only reader and <c>ChatterMessageBrokerExtensions</c>
        /// registers it only when <c>EnableOutboxPollingProcessor</c> is set, so with the poller off nothing will ever
        /// wait on this value and a host carrying a stale out-of-range one must still start.
        /// </summary>
        [Fact]
        public void MustAcceptAnOutOfRangeOutboxProcessingIntervalWhenTheOutboxPollingProcessorIsDisabled()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("-5", enableOutboxPollingProcessor: false);

            var options = ReliabilityOptionsBuilder.FromConfig(services, configuration);

            options.EnableOutboxPollingProcessor.Should().BeFalse();
            options.OutboxProcessingIntervalInMilliseconds.Should().Be(-5);
            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ReliabilityOptions>().Should().BeSameAs(options);
        }

        /// <summary>
        /// The same value the disabled poller keeps: turning the poller on is the whole difference between the two
        /// pins, so the refusal is narrowed to the hosts that run a poller rather than lifted.
        /// </summary>
        [Fact]
        public void MustRefuseTheSameOutOfRangeOutboxProcessingIntervalWhenTheOutboxPollingProcessorIsEnabled()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfigurationWithOutboxProcessingInterval("-5", enableOutboxPollingProcessor: true);

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                      .Which.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.OutboxProcessingIntervalInMilliseconds)}");
            services.Should().BeEmpty();
        }

        /// <summary>
        /// -1 is <c>Timeout.Infinite</c>, so <c>Task.Delay</c> takes it happily and an ENABLED poller then waits for
        /// good. The polling sink is the only thing that can express 'never poll again', and it cannot, so the value
        /// is refused rather than bound into a processor that would silently stop.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(5000)]
        [InlineData(-1)]
        [InlineData(-5)]
        [InlineData(int.MinValue)]
        public void MustAgreeWithTheOutboxPollingSinkAboutAConfiguredProcessingInterval(int interval)
        {
            var services = new ServiceCollection();
            var theSinkCanPollAtIt = TaskDelayAccepts(interval) && interval != Timeout.Infinite;

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithOutboxProcessingInterval(interval.ToString(), enableOutboxPollingProcessor: true));

            if (theSinkCanPollAtIt)
            {
                fromConfig.Should().NotThrow<ConfiguredValueRefusedException>();
            }
            else
            {
                fromConfig.Should().Throw<ConfiguredValueRefusedException>();
                services.Should().BeEmpty();
            }
        }

        /// <summary>
        /// The double converter takes "NaN" and "1e300" straight off a configuration section, so the verdict on each
        /// is the real outbox's rather than a house rule: a ttl the expiry scan expires nothing under, or schedules a
        /// real future expiry with, is accepted, and one it treats a just-processed message as already expired under
        /// is refused. A non-positive ttl is accepted however large its magnitude - the scan returns on it before it
        /// computes anything at all - and so is "1e300", which INVERTED from refused to accepted once the scan
        /// stopped computing an expiry instant the sink could reject: it now compares elapsed minutes against the
        /// ttl, so no magnitude faults it. A NaN and a positive infinity are stated by their own facts below,
        /// because the builder refuses those as intent rather than deriving the verdict from the scan.
        /// </summary>
        [Theory]
        [InlineData("0")]
        [InlineData("10")]
        [InlineData("-5")]
        [InlineData("NaN")]
        [InlineData("-Infinity")]
        [InlineData("1e300")]
        [InlineData("-1e300")]
        public async Task MustAgreeWithTheExpiryScanAboutAConfiguredMinutesToLiveInMemory(string minutesToLiveInMemory)
        {
            var services = new ServiceCollection();
            var observation = await ObserveTheRealExpiryScan(double.Parse(minutesToLiveInMemory, CultureInfo.InvariantCulture));
            var theSinkCanScheduleIt = observation == ExpiryScanObservation.ExpiredNothing
                                    || observation == ExpiryScanObservation.ScheduledAFutureExpiry;

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithMinutesToLiveInMemory(minutesToLiveInMemory));

            if (theSinkCanScheduleIt)
            {
                fromConfig.Should().NotThrow<ConfiguredValueRefusedException>();
            }
            else
            {
                fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                          .Which.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.MinutesToLiveInMemory)}");
                services.Should().BeEmpty();
            }
        }

        /// <summary>
        /// A NaN ttl is the exact value this module measured slipping through the outbox's own <c>ttl &lt;= 0</c>
        /// disable guard - every comparison against a NaN is false - so the scan it reaches is asked here whether it
        /// left everything in place, and it did not. Nothing in the scan can be asked that question on its own, so
        /// the outbox is driven instead.
        /// </summary>
        [Fact]
        public async Task MustRefuseAConfiguredNaNMinutesToLiveInMemoryTheExpiryScanDoesNotDisableItselfFor()
        {
            var services = new ServiceCollection();
            (await ObserveTheRealExpiryScan(double.NaN)).Should().NotBe(ExpiryScanObservation.ExpiredNothing);

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithMinutesToLiveInMemory("NaN"));

            fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                      .Which.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.MinutesToLiveInMemory)}");
            services.Should().BeEmpty();
        }

        /// <summary>
        /// A positive infinity is the one refused ttl the expiry scan itself runs without complaint, so it is stated
        /// apart from the theory above rather than folded into an oracle that would then have to restate the
        /// builder's own rule. The scan expires nothing under it, exactly as a non-positive ttl already says plainly;
        /// it is refused because it names no number of minutes, not because any arithmetic rejects it.
        /// </summary>
        [Fact]
        public async Task MustRefuseAConfiguredInfiniteMinutesToLiveInMemoryTheExpiryScanRunsWithoutFaulting()
        {
            var services = new ServiceCollection();
            (await ObserveTheRealExpiryScan(double.PositiveInfinity)).Should().Be(ExpiryScanObservation.ExpiredNothing);

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithMinutesToLiveInMemory("Infinity"));

            fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                      .Which.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.MinutesToLiveInMemory)}");
            services.Should().BeEmpty();
        }

        /// <summary>
        /// The cap is the in-memory inbox's memory safety valve, so a cap of zero would retain no receipt at all while
        /// the inbox still allocated for every one of them - deduplicating nothing under a setting that reads like a
        /// retention limit. It is refused rather than treated as 'disabled': the deduplication window is where an
        /// operator says how long a receipt lives, and a non-positive window already says 'never expire'.
        /// </summary>
        [Fact]
        public void MustRefuseAConfiguredInMemoryInboxMaxEntriesOfZero()
        {
            var services = new ServiceCollection();

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithInMemoryInboxMaxEntries("0"));

            var refusal = fromConfig.Should().Throw<ConfiguredValueRefusedException>().Which;
            refusal.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.InMemoryInboxMaxEntries)}");
            refusal.RefusedValue.Should().Be(0);
            refusal.RequiredBound.Should().Be("at least 1 entry");
            refusal.ConfigurationPath.Should().Be(ReliabilityOptionsBuilder.ReliabilityOptionsSectionName);
            services.Should().BeEmpty();
        }

        /// <summary>
        /// A negative cap names no number of receipts the inbox could hold, so there is nothing for it to mean.
        /// </summary>
        [Fact]
        public void MustRefuseAConfiguredNegativeInMemoryInboxMaxEntries()
        {
            var services = new ServiceCollection();

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithInMemoryInboxMaxEntries("-5"));

            fromConfig.Should().Throw<ConfiguredValueRefusedException>()
                      .Which.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.InMemoryInboxMaxEntries)}");
            services.Should().BeEmpty();
        }

        /// <summary>
        /// One receipt is the smallest cap the inbox can still deduplicate an immediate redelivery under, so the bound
        /// stops exactly there rather than at some larger house minimum.
        /// </summary>
        [Fact]
        public void MustAcceptTheSmallestInMemoryInboxMaxEntriesTheInboxCanHold()
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithInMemoryInboxMaxEntries("1"));

            options.InMemoryInboxMaxEntries.Should().Be(1);
            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ReliabilityOptions>().Should().BeSameAs(options);
        }

        /// <summary>
        /// A non-positive window disables time-based expiry, the same idiom <see cref="ReliabilityOptions.MinutesToLiveInMemory"/>
        /// already teaches on this very options type. It is accepted whatever its magnitude, and the cap remains the
        /// only thing that then releases a receipt.
        /// </summary>
        [Theory]
        [InlineData("0", 0)]
        [InlineData("-5", -5)]
        public void MustAcceptANonPositiveInMemoryInboxDeduplicationWindow(string configuredWindow, int expectedWindow)
        {
            var services = new ServiceCollection();

            var options = ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithInMemoryInboxDeduplicationWindow(configuredWindow));

            options.InMemoryInboxDeduplicationWindowInMinutes.Should().Be(expectedWindow);
            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ReliabilityOptions>().Should().BeSameAs(options);
        }

        [Fact]
        public void MustNameTheRefusedPropertyValueBoundAndResolvedSectionPathWhenAConfiguredValueIsRefused()
        {
            var services = new ServiceCollection();

            var fromConfig = () => ReliabilityOptionsBuilder.FromConfig(services, BuildConfigurationWithOutboxProcessingInterval("-5", enableOutboxPollingProcessor: true));

            var refusal = fromConfig.Should().Throw<ConfiguredValueRefusedException>().Which;
            refusal.OptionName.Should().Be($"{nameof(ReliabilityOptions)}.{nameof(ReliabilityOptions.OutboxProcessingIntervalInMilliseconds)}");
            refusal.RefusedValue.Should().Be(-5);
            refusal.ConfigurationPath.Should().Be(ReliabilityOptionsBuilder.ReliabilityOptionsSectionName);
            refusal.Message.Should().Contain(refusal.OptionName)
                   .And.Contain("-5")
                   .And.Contain(refusal.ConfigurationPath)
                   .And.Contain(refusal.RequiredBound);
        }

        // BrokeredMessageOutboxProcessor awaits Task.Delay(interval) with no try of its own, so Task.Delay is asked
        // here whether it can run the configured interval rather than having its accepted range restated.
        private static bool TaskDelayAccepts(int millisecondsDelay)
        {
            try
            {
                // INVARIANT: the already-cancelled token keeps this probe from arming a real timer. Task.Delay
                // validates its argument before it observes the token, and the out-of-range candidates above go red
                // the moment that stops being true.
                _ = Task.Delay(millisecondsDelay, new CancellationToken(true));
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        // What a real InMemoryBrokeredMessageOutbox did with a configured ttl. Nothing here restates any part of the
        // scan: the outbox is constructed, driven and then asked what it kept, so this oracle answers out of the sink
        // and stays free to disagree with whatever the builder's own predicate happens to say.
        private enum ExpiryScanObservation
        {
            // Expired nothing: even a message processed a day ago survived it, either because the scan returned
            // before it computed anything or because no elapsed time reaches the ttl.
            ExpiredNothing,
            // Scheduled a real future expiry: the day-old message went and the just-processed one stayed.
            ScheduledAFutureExpiry,
            // Expired a message it had processed this very instant, so the ttl bought that message no life at all.
            ExpiredAJustProcessedMessage,
            // Threw out of the arithmetic it reads the ttl with. No ttl reaches this now that the scan compares
            // elapsed minutes instead of computing an expiry instant; it is kept so a reintroduced overflow is a
            // failure here rather than a silently narrowed oracle.
            Faulted
        }

        private const string _agedMessageId = "processed-a-day-ago";
        private const string _freshMessageId = "processed-this-instant";

        private static async Task<ExpiryScanObservation> ObserveTheRealExpiryScan(double minutesToLiveInMemory)
        {
            var outbox = new InMemoryBrokeredMessageOutbox(NullLogger<InMemoryBrokeredMessageOutbox>.Instance,
                                                           new ReliabilityOptions { MinutesToLiveInMemory = minutesToLiveInMemory });

            await outbox.SendToOutbox(CreateOutbound(_agedMessageId), new TransactionContext());
            await outbox.SendToOutbox(CreateOutbound(_freshMessageId), new TransactionContext());
            var stored = (await outbox.GetUnprocessedMessagesFromOutbox()).ToDictionary(message => message.MessageId);
            stored[_agedMessageId].ProcessedFromOutboxAtUtc = DateTime.UtcNow.AddDays(-1);

            try
            {
                // Stamps the fresh message with the current time and runs the expiry scan over both of them.
                await outbox.UpdateProcessedDate(stored[_freshMessageId]);
            }
            catch (ArgumentOutOfRangeException)
            {
                return ExpiryScanObservation.Faulted;
            }

            if (!await RemainsInTheOutbox(outbox, _freshMessageId))
            {
                return ExpiryScanObservation.ExpiredAJustProcessedMessage;
            }

            return await RemainsInTheOutbox(outbox, _agedMessageId)
                ? ExpiryScanObservation.ExpiredNothing
                : ExpiryScanObservation.ScheduledAFutureExpiry;
        }

        // The outbox publishes no processed-message query, so retention is read the way the outbox's own tests read
        // it: a message it still holds owns its id, and a second send under that id is refused.
        private static async Task<bool> RemainsInTheOutbox(InMemoryBrokeredMessageOutbox outbox, string messageId)
        {
            try
            {
                await outbox.SendToOutbox(CreateOutbound(messageId), new TransactionContext());
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private static OutboundBrokeredMessage CreateOutbound(string messageId)
            => new OutboundBrokeredMessage(messageId,
                                           new byte[] { 1, 2, 3 },
                                           new Dictionary<string, object>(),
                                           "destination",
                                           new TextPlainBodyConverter());

        private static IConfiguration BuildConfigurationWithMinutesToLiveInMemory(string minutesToLiveInMemory)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:MinutesToLiveInMemory"] = minutesToLiveInMemory
                })
                .Build();

        // The interval and the poller switch travel together because only the poller reads the interval, so every
        // test that states one states the other.
        private static IConfiguration BuildConfigurationWithOutboxProcessingInterval(string interval, bool enableOutboxPollingProcessor)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:OutboxProcessingIntervalInMilliseconds"] = interval,
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:EnableOutboxPollingProcessor"] = enableOutboxPollingProcessor.ToString()
                })
                .Build();

        private static IConfiguration BuildConfigurationWithInMemoryInboxMaxEntries(string maxEntries)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:InMemoryInboxMaxEntries"] = maxEntries
                })
                .Build();

        private static IConfiguration BuildConfigurationWithInMemoryInboxDeduplicationWindow(string windowInMinutes)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:InMemoryInboxDeduplicationWindowInMinutes"] = windowInMinutes
                })
                .Build();

        private static IConfiguration BuildConfigurationWithRouteMessagesToOutbox()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:RouteMessagesToOutbox"] = "true"
                })
                .Build();
    }
}
