using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.AzureServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using ServiceBusClient = Azure.Messaging.ServiceBus.ServiceBusClient;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.DependencyInjection.UsingChatterAzureServiceBusExtensions
{
    // Verifies the DI-time cross-entity-transactions opt-in, effective-flag computation, and single-top-level
    // -entity startup guard WITHOUT a live broker. The shared ServiceBusClient is constructed (and the guard
    // runs) at first resolve of ServiceBusClient — no Azure connection is made — so a placeholder SAS
    // connection string is sufficient and these are fast unit [Fact]s, not Docker-gated integration tests.
    public class WhenConfiguringCrossEntityTransactions : Testing.Core.Context
    {
        private const string _connectionString =
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret";

        private sealed class FirstCommand : ICommand { }
        private sealed class SecondCommand : ICommand { }

        private static IConfiguration EmptyConfig()
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

        // Builds a configuration whose Chatter:Infrastructure:AzureServiceBus section carries the connection
        // string plus the supplied EnableCrossEntityTransactions value, so the opt-in is exercised purely via
        // config binding (ServiceBusOptionsBuilder.UseConfig -> plain section.Bind(options) into the
        // default-initialized ServiceBusOptions, with the internal RetryPolicy bound explicitly from its own
        // subsection), with NO fluent WithCrossEntityTransactions() call.
        private static IConfiguration CrossEntityConfig(bool enableCrossEntityTransactions)
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Chatter:Infrastructure:AzureServiceBus:ConnectionString"] = _connectionString,
                ["Chatter:Infrastructure:AzureServiceBus:EnableCrossEntityTransactions"] =
                    enableCrossEntityTransactions.ToString(),
            }).Build();

        // Builds a configuration whose Chatter:Infrastructure:AzureServiceBus section carries the connection
        // string plus a GLOBAL MaxConcurrentCalls, so the global value arrives purely by config binding with NO
        // fluent WithMaxConcurrentCalls call — the source a per-receiver value has to beat.
        private static IConfiguration MaxConcurrentCallsConfig(int maxConcurrentCalls)
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Chatter:Infrastructure:AzureServiceBus:ConnectionString"] = _connectionString,
                ["Chatter:Infrastructure:AzureServiceBus:MaxConcurrentCalls"] = maxConcurrentCalls.ToString(),
            }).Build();

        // Builds services where the ASB opt-in comes ONLY from configuration binding (no fluent
        // WithConnectionString / WithCrossEntityTransactions): the section is bound by the options builder.
        private static ServiceCollection BuildServicesFromConfig(
            IConfiguration configuration,
            Action<ServiceBusOptionsBuilder> configure)
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddChatterCqrs(configuration, typeof(WhenConfiguringCrossEntityTransactions))
                    .AddMessageBrokers()
                    .AddAzureServiceBus(sb => configure(sb));

            return services;
        }

        private static ServiceCollection BuildServices(Action<ServiceBusOptionsBuilder> configure)
            => BuildServices(configure, null);

        private static ServiceCollection BuildServices(
            Action<ServiceBusOptionsBuilder> configure,
            Action<MessageBrokerOptionsBuilder> configureMessageBrokers)
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddChatterCqrs(EmptyConfig(), typeof(WhenConfiguringCrossEntityTransactions))
                    .AddMessageBrokers(configureMessageBrokers)
                    .AddAzureServiceBus(sb =>
                    {
                        sb.WithConnectionString(_connectionString);
                        configure(sb);
                    });

            return services;
        }

        [Fact]
        public async Task MustConstructSharedClientWithoutTrippingGuardForTwoNonAtomicQueueReceivers()
        {
            // (a) Default (no opt-in, two distinct non-atomic queue receivers): cross-entity is OFF, so the
            // shared client is constructed and the guard does NOT throw — both receivers can run.
            await using var provider = BuildServices(sb =>
            {
                sb.AddQueueReceiver<FirstCommand>("queue-a");
                sb.AddQueueReceiver<SecondCommand>("queue-b");
            }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should().NotThrow();
            resolveClient().Should().NotBeNull();
        }

        [Fact]
        public async Task MustThrowConfigurationGuardWhenCrossEntityEnabledWithMultipleTopLevelEntities()
        {
            // (b) Explicit WithCrossEntityTransactions() + two distinct top-level entities: the unsupportable
            // combination fails fast and loud at client build with a clear configuration exception naming the
            // conflicting entities.
            await using var provider = BuildServices(sb =>
            {
                sb.WithCrossEntityTransactions();
                sb.AddQueueReceiver<FirstCommand>("queue-a");
                sb.AddQueueReceiver<SecondCommand>("queue-b");
            }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*single top-level receiver entity*")
                .WithMessage("*queue-a*")
                .WithMessage("*queue-b*");
        }

        [Fact]
        public async Task MustConstructSharedClientForSingleFullAtomicityReceiver()
        {
            // (c) A single FullAtomicityViaInfrastructure receiver auto-enables cross-entity transactions; with
            // only one top-level entity the guard does not trip and the client is constructed.
            await using var provider = BuildServices(sb =>
                sb.AddQueueReceiver<FirstCommand>("queue-a", transactionMode: TransactionMode.FullAtomicityViaInfrastructure))
                .BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should().NotThrow();
            resolveClient().Should().NotBeNull();
        }

        [Fact]
        public async Task MustThrowConfigurationGuardWhenFullAtomicitySpansMultipleTopLevelEntities()
        {
            // A FullAtomicityViaInfrastructure receiver auto-enables cross-entity transactions; pairing it with
            // a second distinct top-level entity is the silent-hang scenario the guard converts into a loud
            // startup failure.
            await using var provider = BuildServices(sb =>
            {
                sb.AddQueueReceiver<FirstCommand>("queue-a", transactionMode: TransactionMode.FullAtomicityViaInfrastructure);
                sb.AddQueueReceiver<SecondCommand>("queue-b");
            }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*single top-level receiver entity*");
        }

        [Fact]
        public async Task MustNotTripGuardForTwoSubscriptionsOnSameTopic()
        {
            // Two subscriptions on the SAME topic share one top-level entity, so even with cross-entity on they
            // do not count as distinct entities and the guard does not trip.
            await using var provider = BuildServices(sb =>
            {
                sb.WithCrossEntityTransactions();
                sb.AddTopicSubscription<FirstEvent>("shared-topic", "sub-1");
                sb.AddTopicSubscription<SecondEvent>("shared-topic", "sub-2");
            }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should().NotThrow();
            resolveClient().Should().NotBeNull();
        }

        [Fact]
        public async Task MustConstructSharedClientWhenGlobalFullAtomicityWithSingleReceiverWithoutPerCallMode()
        {
            // Regression guard for the bug: a GLOBAL WithTransactionMode(FullAtomicityViaInfrastructure) set on
            // MessageBrokerOptions, with a single queue receiver carrying NO per-call mode, must auto-enable
            // cross-entity transactions via the inherited global mode. With one top-level entity the guard does
            // not trip and the client is constructed.
            await using var provider = BuildServices(
                sb => sb.AddQueueReceiver<FirstCommand>("queue-a"),
                mb => mb.WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure))
                .BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should().NotThrow();
            resolveClient().Should().NotBeNull();
        }

        [Fact]
        public async Task MustThrowConfigurationGuardWhenGlobalFullAtomicitySpansMultipleTopLevelEntities()
        {
            // Global WithTransactionMode(FullAtomicityViaInfrastructure) folds into each receiver's effective
            // mode, so two distinct top-level queue entities (neither with a per-call mode) trip the
            // single-top-level-entity guard exactly as per-call atomicity would.
            await using var provider = BuildServices(
                sb =>
                {
                    sb.AddQueueReceiver<FirstCommand>("queue-a");
                    sb.AddQueueReceiver<SecondCommand>("queue-b");
                },
                mb => mb.WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure))
                .BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*single top-level receiver entity*")
                .WithMessage("*queue-a*")
                .WithMessage("*queue-b*");
        }

        [Fact]
        public async Task MustConstructSharedClientWhenGlobalReceiveOnlyWithTwoNonAtomicReceivers()
        {
            // Global ReceiveOnly (the default global mode) keeps cross-entity OFF, so two distinct non-atomic
            // top-level entities are allowed and the guard does not trip — existing multi-receiver hosts are
            // unaffected by the global-mode fold-in.
            await using var provider = BuildServices(
                sb =>
                {
                    sb.AddQueueReceiver<FirstCommand>("queue-a");
                    sb.AddQueueReceiver<SecondCommand>("queue-b");
                },
                mb => mb.WithTransactionMode(TransactionMode.ReceiveOnly))
                .BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should().NotThrow();
            resolveClient().Should().NotBeNull();
        }

        [Fact]
        public async Task MustBindCrossEntityOptInFromConfigurationAndTripGuardForMultipleTopLevelEntities()
        {
            // Config-only opt-in: EnableCrossEntityTransactions = true is set purely via configuration binding
            // (no fluent WithCrossEntityTransactions()). The flag must bind from the section, so pairing it with
            // two distinct top-level entities trips the single-top-level-entity guard exactly as the fluent
            // opt-in does. Regression guard for the internal-setter binding gap (config opt-in was silently
            // ignored because ConfigurationBinder skips non-public setters).
            await using var provider = BuildServicesFromConfig(
                CrossEntityConfig(enableCrossEntityTransactions: true),
                sb =>
                {
                    sb.AddQueueReceiver<FirstCommand>("queue-a");
                    sb.AddQueueReceiver<SecondCommand>("queue-b");
                }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*single top-level receiver entity*")
                .WithMessage("*queue-a*")
                .WithMessage("*queue-b*");
        }

        [Fact]
        public async Task MustNotEnableCrossEntityWhenConfigOptInAbsentForMultipleTopLevelEntities()
        {
            // Config binds EnableCrossEntityTransactions = false (the default): cross-entity stays OFF, so two
            // distinct non-atomic top-level entities are allowed and the guard does not trip — the config-bound
            // flag is genuinely honored in both directions.
            await using var provider = BuildServicesFromConfig(
                CrossEntityConfig(enableCrossEntityTransactions: false),
                sb =>
                {
                    sb.AddQueueReceiver<FirstCommand>("queue-a");
                    sb.AddQueueReceiver<SecondCommand>("queue-b");
                }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should().NotThrow();
            resolveClient().Should().NotBeNull();
        }

        [Fact]
        public async Task MustHonorExplicitFluentDisableOverConfigEnabled()
        {
            // Regression guard (Codex P2): config binds EnableCrossEntityTransactions = true, but the app
            // explicitly calls WithCrossEntityTransactions(false) to force it off. The explicit fluent value
            // must win over the config-bound value, so the resolved option is false.
            await using var provider = BuildServicesFromConfig(
                CrossEntityConfig(enableCrossEntityTransactions: true),
                sb => sb.WithCrossEntityTransactions(false)).BuildServiceProvider();

            var options = provider.GetRequiredService<ServiceBusOptions>();

            options.EnableCrossEntityTransactions.Should().BeFalse();
        }

        [Fact]
        public async Task MustKeepConfigEnabledWhenNoFluentCall()
        {
            // Config binds EnableCrossEntityTransactions = true and no fluent WithCrossEntityTransactions()
            // call is made: the config-bound value is left untouched, so the resolved option stays true.
            await using var provider = BuildServicesFromConfig(
                CrossEntityConfig(enableCrossEntityTransactions: true),
                sb => { }).BuildServiceProvider();

            var options = provider.GetRequiredService<ServiceBusOptions>();

            options.EnableCrossEntityTransactions.Should().BeTrue();
        }

        [Fact]
        public async Task MustEnableViaFluentWhenNoConfig()
        {
            // No config opt-in, but the app explicitly calls WithCrossEntityTransactions(true): the explicit
            // fluent value applies, so the resolved option is true.
            await using var provider = BuildServices(
                sb => sb.WithCrossEntityTransactions(true)).BuildServiceProvider();

            var options = provider.GetRequiredService<ServiceBusOptions>();

            options.EnableCrossEntityTransactions.Should().BeTrue();
        }

        [Fact]
        public async Task MustDefaultToDisabledWhenNoConfigAndNoFluentCall()
        {
            // Neither config opt-in nor a fluent WithCrossEntityTransactions() call: the option falls back to
            // its default of false.
            await using var provider = BuildServices(sb => { }).BuildServiceProvider();

            var options = provider.GetRequiredService<ServiceBusOptions>();

            options.EnableCrossEntityTransactions.Should().BeFalse();
        }

        // ----------------------------------------------------------------- (F3) attribute/core-registered receivers reach the guard

        // Registers ASB receivers via the CORE AddReceiver route (MessageBrokerOptionsBuilder), which never
        // calls AddQueueReceiver/AddTopicSubscription and so historically bypassed the ASB ServiceBusReceiverRegistry
        // entirely — the same registration path the [BrokeredMessageAttribute] assembly scan converges on
        // (ChatterMessageBrokerExtensions.AddReceiverImpl). STEP-003's PopulateFromDiscoveredReceivers now folds
        // these into the ASB registry so the cross-entity guard counts them. Cross-entity is forced on via the
        // fluent ServiceBus opt-in. A queue receiver's sending path equals its receiver path (the queue IS the
        // top-level entity); a topic subscription's sending path is the distinct topic.
        // Mirrors the descriptor lookup PopulateFromDiscoveredReceivers performs (ServiceType ==
        // typeof(IDiscoveredReceiverRegistry), ImplementationInstance narrowed with `as`). Returns null on exactly
        // the misses the production read swallows: a null read returns EARLY, so nothing is stamped and nothing is
        // folded. Asserting a non-null result pins that the core had already PUBLISHED the registry, in a shape ASB's
        // registration-time read can see, by the time ASB read it.
        private static IDiscoveredReceiverRegistry ReadDiscoveredRegistryOffDescriptors(IServiceCollection services)
            => services.FirstOrDefault(d => d.ServiceType == typeof(IDiscoveredReceiverRegistry))?
                       .ImplementationInstance as IDiscoveredReceiverRegistry;

        // ASB's OWN receiver registry. A core-route receiver reaches it ONLY through the
        // PopulateFromDiscoveredReceivers fold, which runs past that null-guard, so its contents are production-side
        // evidence that the discovered-registry read succeeded and the receiver was claimed by ASB.
        private static global::Chatter.MessageBrokers.AzureServiceBus.DependencyInjection.ServiceBusReceiverRegistry
            ResolveAsbReceiverRegistry(IServiceProvider provider)
            => provider.GetRequiredService<
                global::Chatter.MessageBrokers.AzureServiceBus.DependencyInjection.ServiceBusReceiverRegistry>();

        private static ServiceCollection BuildServicesWithCoreReceivers(
            Action<ServiceBusOptionsBuilder> configureServiceBus,
            Action<MessageBrokerOptionsBuilder> configureReceivers)
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddChatterCqrs(EmptyConfig(), typeof(WhenConfiguringCrossEntityTransactions))
                    .AddMessageBrokers(configureReceivers)
                    .AddAzureServiceBus(sb =>
                    {
                        sb.WithConnectionString(_connectionString);
                        configureServiceBus(sb);
                    });

            return services;
        }

        [Fact]
        public async Task MustTripGuardForCoreRegisteredReceiversOnMultipleTopLevelEntities()
        {
            // F3: two ASB receivers registered ONLY via the core AddReceiver route (no AddQueueReceiver call) on
            // two DISTINCT top-level queue entities, with cross-entity forced on. Before STEP-003 these bypassed
            // the ASB registry, so the guard saw zero entities and did NOT trip; now PopulateFromDiscoveredReceivers
            // folds them in, so the unsupportable combination fails fast at client build — proving the
            // attribute/core route is counted.
            await using var provider = BuildServicesWithCoreReceivers(
                sb => sb.WithCrossEntityTransactions(),
                mb =>
                {
                    mb.AddReceiver<FirstCommand>("core-queue-a", senderPath: "core-queue-a", infrastructureType: ASBMessageContext.InfrastructureType);
                    mb.AddReceiver<SecondCommand>("core-queue-b", senderPath: "core-queue-b", infrastructureType: ASBMessageContext.InfrastructureType);
                }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*single top-level receiver entity*")
                .WithMessage("*core-queue-a*")
                .WithMessage("*core-queue-b*");
        }

        [Fact]
        public async Task MustNotTripGuardForCoreRegisteredSubscriptionsOnSameTopic()
        {
            // F3: two core-registered ASB topic subscriptions on the SAME topic (distinct sending path = the
            // topic) share one top-level entity, so even folded into the ASB registry they count once and the
            // guard does not trip. Mirrors the explicit AddTopicSubscription same-topic case but via the core
            // route, proving InferTopLevelEntity resolves the topic (not the subscription) as the top-level entity.
            await using var provider = BuildServicesWithCoreReceivers(
                sb => sb.WithCrossEntityTransactions(),
                mb =>
                {
                    mb.AddReceiver<FirstEvent>("core-sub-1", senderPath: "core-shared-topic", infrastructureType: ASBMessageContext.InfrastructureType);
                    mb.AddReceiver<SecondEvent>("core-sub-2", senderPath: "core-shared-topic", infrastructureType: ASBMessageContext.InfrastructureType);
                }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should().NotThrow();
            resolveClient().Should().NotBeNull();
        }

        [Fact]
        public async Task MustTripGuardWhenCoreRegisteredReceiverUsesGlobalFullAtomicity()
        {
            // F3: a core-registered ASB receiver with NO per-call transaction mode is folded in with null mode,
            // inheriting the GLOBAL FullAtomicityViaInfrastructure mode — which auto-enables cross-entity — so a
            // second distinct top-level entity trips the guard without any explicit WithCrossEntityTransactions().
            // Proves the folded receivers participate in the effective-mode (global fold-in) computation, not just
            // the explicit opt-in.
            await using var provider = BuildServicesWithCoreReceivers(
                sb => { },
                mb =>
                {
                    mb.WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure);
                    mb.AddReceiver<FirstCommand>("core-queue-a", senderPath: "core-queue-a", infrastructureType: ASBMessageContext.InfrastructureType);
                    mb.AddReceiver<SecondCommand>("core-queue-b", senderPath: "core-queue-b", infrastructureType: ASBMessageContext.InfrastructureType);
                }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*single top-level receiver entity*");
        }

        // ----------------------------------------------------------------- (F4) host-fail-fast without a live broker

        [Fact]
        public async Task MustThrowPlainInvalidOperationExceptionForUnsupportableComboWithoutBroker()
        {
            // F4: the unsupportable >1-top-level-entity + cross-entity combination fails fast at client resolve
            // with a PLAIN InvalidOperationException (the cross-entity guard) — NOT an Azure SDK connection error
            // — proving the guard fires at DI/build time with no live namespace. The exception type is the bare
            // InvalidOperationException, not a derived/aggregate type.
            await using var provider = BuildServices(sb =>
            {
                sb.WithCrossEntityTransactions();
                sb.AddQueueReceiver<FirstCommand>("queue-a");
                sb.AddQueueReceiver<SecondCommand>("queue-b");
            }).BuildServiceProvider();

            Action resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            var thrown = resolveClient.Should().Throw<InvalidOperationException>().Which;
            thrown.GetType().Should().Be<InvalidOperationException>();
        }

        [Fact]
        public async Task MustNotThrowForSupportableSingleEntityHostWithoutBroker()
        {
            // F4: a valid single-top-level-entity host with cross-entity on resolves the client without throwing,
            // again with no live broker — confirming fail-fast is scoped to the unsupportable combination only.
            await using var provider = BuildServices(sb =>
            {
                sb.WithCrossEntityTransactions();
                sb.AddQueueReceiver<FirstCommand>("queue-a");
            }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should().NotThrow();
            resolveClient().Should().NotBeNull();
        }

        // ----------------------------------------------------------------- MaxConcurrentCalls source-of-truth flow

        [Fact]
        public async Task MustStampGlobalMaxConcurrentCallsOntoDiscoveredAsbReceiverOptions()
        {
            // The global ServiceBusOptions.MaxConcurrentCalls (set fluently to 7) is stamped by
            // PopulateFromDiscoveredReceivers onto each ASB receiver's RETAINED live ReceiverOptions, so the value
            // reaches the receiver init seam. Asserted on the live ReceiverOptions held in IDiscoveredReceiverRegistry
            // — the same instance BrokeredMessageReceiver reads MaxConcurrentCalls from at startup.
            var services = BuildServicesWithCoreReceivers(
                sb => sb.WithMaxConcurrentCalls(7),
                mb => mb.AddReceiver<FirstCommand>("core-queue-a", senderPath: "core-queue-a", infrastructureType: ASBMessageContext.InfrastructureType));
            await using var provider = services.BuildServiceProvider();

            // The registry read is an `as` cast that yields NULL on a miss, and a null read stamps nothing at all,
            // so pin that the read SUCCEEDED and landed on the same registry consumers resolve.
            var discoveredRegistry = ReadDiscoveredRegistryOffDescriptors(services);
            discoveredRegistry.Should().NotBeNull();
            discoveredRegistry.Should().BeSameAs(provider.GetRequiredService<IDiscoveredReceiverRegistry>());

            // The core-route receiver reached ASB's own registry, which happens only inside the fold that runs past
            // the null-guard — production-side proof the read found the registry rather than nothing.
            ResolveAsbReceiverRegistry(provider).DistinctTopLevelEntities().Should().Contain("core-queue-a");

            var asbReceiver = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "core-queue-a");

            // The stamp came from the FINALIZED published ServiceBusOptions, not a coincidental literal.
            asbReceiver.MaxConcurrentCalls.Should()
                .Be(provider.GetRequiredService<ServiceBusOptions>().MaxConcurrentCalls);
            asbReceiver.MaxConcurrentCalls.Should().Be(7);
        }

        [Fact]
        public async Task MustLeaveDiscoveredAsbReceiverMaxConcurrentCallsAtDefaultWhenGlobalUnset()
        {
            // Zero-behavior-change guard: with the global MaxConcurrentCalls unset (default 1), the stamp leaves
            // each ASB receiver's effective MaxConcurrentCalls at the default 1 — proving the flow does not alter
            // existing single-call (sequential) receive behavior for hosts that never configure it.
            var services = BuildServicesWithCoreReceivers(
                sb => { },
                mb => mb.AddReceiver<FirstCommand>("core-queue-a", senderPath: "core-queue-a", infrastructureType: ASBMessageContext.InfrastructureType));
            await using var provider = services.BuildServiceProvider();

            // A stamped 1 and an unstamped 1 are indistinguishable, so this arm would pass even if the registry read
            // had found NOTHING. Close that ambiguity: the read succeeded, and the receiver was folded into ASB's
            // own registry — which happens only past the null-guard, inside the same loop that stamps.
            var discoveredRegistry = ReadDiscoveredRegistryOffDescriptors(services);
            discoveredRegistry.Should().NotBeNull();
            discoveredRegistry.Should().BeSameAs(provider.GetRequiredService<IDiscoveredReceiverRegistry>());
            ResolveAsbReceiverRegistry(provider).DistinctTopLevelEntities().Should().Contain("core-queue-a");

            var asbReceiver = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "core-queue-a");

            asbReceiver.MaxConcurrentCalls.Should()
                .Be(provider.GetRequiredService<ServiceBusOptions>().MaxConcurrentCalls);
            asbReceiver.MaxConcurrentCalls.Should().Be(1);
        }

        // ----------------------------------------------------------------- (ADR-0014) session concurrency and the per-receiver knob

        [Fact]
        public async Task MustGiveSessionReceiverTheGlobalMaxConcurrentCallsJustLikeANonSessionReceiver()
        {
            // ADR-0014: in session mode MaxConcurrentCalls means CONCURRENT SESSIONS, so a session-mode receiver
            // is stamped with the global value (set fluently to 7) exactly as its non-session neighbour is. The
            // clamp that overwrote a session receiver's live ReceiverOptions.MaxConcurrentCalls with 1 is gone:
            // the one-message-per-session property now belongs to each multiplexed single-session child, not to
            // the receiver. Asserted on the live ReceiverOptions held in IDiscoveredReceiverRegistry, the same
            // instance BrokeredMessageReceiver reads at init.
            var services = BuildServices(sb =>
            {
                sb.WithMaxConcurrentCalls(7);
                sb.AddSessionQueueReceiver<FirstCommand>("session-queue");
                sb.AddQueueReceiver<SecondCommand>("normal-queue");
            });
            await using var provider = services.BuildServiceProvider();

            var discoveredRegistry = ReadDiscoveredRegistryOffDescriptors(services);
            discoveredRegistry.Should().NotBeNull();
            discoveredRegistry.Should().BeSameAs(provider.GetRequiredService<IDiscoveredReceiverRegistry>());

            var sessionReceiver = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "session-queue");
            var normalReceiver = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "normal-queue");

            // Both carry the FINALIZED published global, not a coincidental literal.
            sessionReceiver.MaxConcurrentCalls.Should()
                .Be(provider.GetRequiredService<ServiceBusOptions>().MaxConcurrentCalls);
            sessionReceiver.MaxConcurrentCalls.Should().Be(7);
            normalReceiver.MaxConcurrentCalls.Should()
                .Be(provider.GetRequiredService<ServiceBusOptions>().MaxConcurrentCalls);
            normalReceiver.MaxConcurrentCalls.Should().Be(7);
        }

        [Fact]
        public async Task MustGiveSessionTopicSubscriptionAndItsSiblingOnTheSameTopicTheGlobal()
        {
            // ADR-0014: a session-enabled subscription and a normal subscription on the SAME topic are distinct
            // receivers, and neither is singled out any more — both are stamped with the global 7. Retained from
            // the clamp era because the session subscription's raw-vs-canonical path handling still runs here:
            // the stamp resolves this subscription's own entry, not its sibling's.
            await using var provider = BuildServices(sb =>
            {
                sb.WithMaxConcurrentCalls(7);
                sb.AddSessionTopicSubscription<FirstEvent>("shared-topic", "session-sub");
                sb.AddTopicSubscription<SecondEvent>("shared-topic", "normal-sub");
            }).BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();
            var sessionSubscription = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "session-sub");
            var normalSubscription = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "normal-sub");

            sessionSubscription.MaxConcurrentCalls.Should().Be(7);
            normalSubscription.MaxConcurrentCalls.Should().Be(7);
        }

        [Fact]
        public async Task MustGiveStandaloneSessionTopicSubscriptionTheGlobalAboveOne()
        {
            // ADR-0014: a lone session topic subscription inherits the global 7 — nothing overwrites it with 1
            // any more. The standalone case, with no sibling subscription present, so the value cannot have come
            // from a neighbouring receiver's entry.
            await using var provider = BuildServices(sb =>
            {
                sb.WithMaxConcurrentCalls(7);
                sb.AddSessionTopicSubscription<FirstEvent>("session-topic", "session-sub");
            }).BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();
            var sessionSubscription = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "session-sub");

            sessionSubscription.MaxConcurrentCalls.Should().Be(7);
        }

        [Fact]
        public async Task MustGiveSessionTopicSubscriptionAndNormalSubscriptionOnDifferentTopicsTheGlobal()
        {
            // ADR-0014: a session subscription on one topic and a normal subscription on a DISTINCT topic both
            // inherit the global 7. Complements the same-topic sibling fact above: no receiver is singled out by
            // session mode, and neither topic's entry leaks into the other's stamp.
            await using var provider = BuildServices(sb =>
            {
                sb.WithMaxConcurrentCalls(7);
                sb.AddSessionTopicSubscription<FirstEvent>("session-topic", "session-sub");
                sb.AddTopicSubscription<SecondEvent>("normal-topic", "normal-sub");
            }).BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();
            var sessionSubscription = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "session-sub");
            var normalSubscription = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "normal-sub");

            sessionSubscription.MaxConcurrentCalls.Should().Be(7);
            normalSubscription.MaxConcurrentCalls.Should().Be(7);
        }

        [Fact]
        public async Task MustLeaveSessionReceiverAtOneWhenGlobalMaxConcurrentCallsIsUnset()
        {
            // ADR-0014 zero-behaviour-change default: with the global MaxConcurrentCalls unset (default 1), a
            // session receiver is stamped 1, so ServiceBusReceiver uses the bare single-session adapter with no
            // multiplexer in the path. Removing the clamp raises nothing for a host that never configured
            // concurrency; it only stops overriding hosts that did.
            var services = BuildServices(sb => sb.AddSessionQueueReceiver<FirstCommand>("session-queue"));
            await using var provider = services.BuildServiceProvider();

            // Every value in this arm is 1, so the assertion below cannot tell a no-op clamp from a registry read
            // that found nothing. Pin the read itself: the descriptor was found, narrowed non-null, and is the very
            // registry consumers resolve.
            var discoveredRegistry = ReadDiscoveredRegistryOffDescriptors(services);
            discoveredRegistry.Should().NotBeNull();
            discoveredRegistry.Should().BeSameAs(provider.GetRequiredService<IDiscoveredReceiverRegistry>());

            var sessionReceiver = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "session-queue");

            sessionReceiver.MaxConcurrentCalls.Should()
                .Be(provider.GetRequiredService<ServiceBusOptions>().MaxConcurrentCalls);
            sessionReceiver.MaxConcurrentCalls.Should().Be(1);
        }

        [Fact]
        public async Task MustLetAStatedPerReceiverValueBeatTheGlobalOnEveryRegistrationMethod()
        {
            // SPECIFICITY BEATS SOURCE (ADR-0014): a value the receiver STATES on its own registration wins over
            // the global one. The knob is on all four registration methods with one meaning per mode — concurrent
            // messages for a non-session receiver, concurrent sessions for a session receiver — so all four are
            // exercised against one global of 7.
            await using var provider = BuildServices(sb =>
            {
                sb.WithMaxConcurrentCalls(7);
                sb.AddQueueReceiver<FirstCommand>("stated-queue", 2);
                sb.AddTopicSubscription<FirstEvent>("stated-topic", "stated-sub", 3);
                sb.AddSessionQueueReceiver<SecondCommand>("stated-session-queue", 4);
                sb.AddSessionTopicSubscription<SecondEvent>("stated-session-topic", "stated-session-sub", 5);
            }).BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();

            // The global really is 7, so every value below is an override rather than a coincidence.
            provider.GetRequiredService<ServiceBusOptions>().MaxConcurrentCalls.Should().Be(7);

            StatedReceiver(discoveredRegistry, "stated-queue").MaxConcurrentCalls.Should().Be(2);
            StatedReceiver(discoveredRegistry, "stated-sub").MaxConcurrentCalls.Should().Be(3);
            StatedReceiver(discoveredRegistry, "stated-session-queue").MaxConcurrentCalls.Should().Be(4);
            StatedReceiver(discoveredRegistry, "stated-session-sub").MaxConcurrentCalls.Should().Be(5);
        }

        [Fact]
        public async Task MustHonourAStatedOneAgainstAGlobalAboveOne()
        {
            // A stated 1 is a deliberate choice of serial processing, not an unset value: it must beat a global 7
            // in the SAME direction as a stated 9 would. The nullable stated value is what distinguishes "stated
            // the default" from "stated nothing" — a plain int could not tell those apart.
            await using var provider = BuildServices(sb =>
            {
                sb.WithMaxConcurrentCalls(7);
                sb.AddSessionQueueReceiver<FirstCommand>("serial-session-queue", 1);
                sb.AddQueueReceiver<SecondCommand>("inheriting-queue");
            }).BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();

            StatedReceiver(discoveredRegistry, "serial-session-queue").MaxConcurrentCalls.Should().Be(1);
            // The neighbour proves the global was 7 all along, so the 1 above is the stated value winning and not
            // a global that happened to be 1.
            StatedReceiver(discoveredRegistry, "inheriting-queue").MaxConcurrentCalls.Should().Be(7);
        }

        [Fact]
        public async Task MustLetAStatedPerReceiverValueBeatAGlobalBoundFromConfiguration()
        {
            // SPECIFICITY BEATS SOURCE, the config arm: the global arrives purely by section binding (no fluent
            // WithMaxConcurrentCalls call) and the per-receiver value still wins. This is a DIFFERENT axis from
            // the module's fluent-beats-configuration rule, which resolves two SOURCES for ONE value; this
            // resolves two SCOPES, and the narrower scope wins whatever source either value came from.
            await using var provider = BuildServicesFromConfig(MaxConcurrentCallsConfig(7), sb =>
            {
                sb.AddSessionQueueReceiver<FirstCommand>("stated-session-queue", 3);
                sb.AddQueueReceiver<SecondCommand>("inheriting-queue");
            }).BuildServiceProvider();

            // The global came from configuration, not from a fluent call.
            provider.GetRequiredService<ServiceBusOptions>().MaxConcurrentCalls.Should().Be(7);

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();

            StatedReceiver(discoveredRegistry, "stated-session-queue").MaxConcurrentCalls.Should().Be(3);
            StatedReceiver(discoveredRegistry, "inheriting-queue").MaxConcurrentCalls.Should().Be(7);
        }

        [Fact]
        public async Task MustGiveADiscoveredReceiverTheGlobalWhileAStatedNeighbourKeepsItsOwnValue()
        {
            // A receiver registered through the core route (the shape the [BrokeredMessageAttribute] assembly scan
            // produces) states nothing, so it inherits the global 7 — while a fluently-registered neighbour that
            // DID state a value keeps it. Pins that "stated nothing" and "stated a value" are distinguished
            // per-receiver rather than host-wide.
            await using var provider = BuildServicesWithCoreReceivers(
                sb =>
                {
                    sb.WithMaxConcurrentCalls(7);
                    sb.AddQueueReceiver<SecondCommand>("stated-queue", 2);
                },
                mb => mb.AddReceiver<FirstCommand>("core-queue-a", senderPath: "core-queue-a", infrastructureType: ASBMessageContext.InfrastructureType))
                .BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();

            StatedReceiver(discoveredRegistry, "core-queue-a").MaxConcurrentCalls.Should().Be(7);
            StatedReceiver(discoveredRegistry, "stated-queue").MaxConcurrentCalls.Should().Be(2);
        }

        [Fact]
        public async Task MustBindEveryRegistrationShapeToTheIntendedOverloadWithoutAmbiguity()
        {
            // COMPILE-TIME proof that the per-receiver overloads leave every call shape unambiguous, in BOTH
            // directions. maxConcurrentCalls sits IMMEDIATELY AFTER the required path parameter(s) and is itself
            // REQUIRED, so overload resolution separates the two candidates by the TYPE of the argument in that
            // position: an int reaches only the new overload, a string reaches only the old one, and a call that
            // omits the position entirely is not applicable to the new overload at all — so CS0121 never arises.
            // Because the parameter is no longer trailing, the remaining parameters keep their defaults (CS1737
            // does not fire), which is what makes the one-knob call site spell ONE extra argument, not four.
            // Compiling IS the assertion for the binding; the stamped value says WHICH overload ran — an
            // inherited 7 means the original overload, a stated value means the per-receiver one.
            await using var provider = BuildServices(sb =>
            {
                sb.WithMaxConcurrentCalls(7);

                // Omitted entirely: only the original overload is applicable.
                sb.AddQueueReceiver<FirstCommand>("legacy-queue");
                // A string in the second position does not convert to int?, so only the original applies.
                sb.AddQueueReceiver<FirstCommand>("errors-positional-queue", "errors");
                // Named arguments of the original shape only: the new overload cannot be satisfied.
                sb.AddQueueReceiver<FirstCommand>("named-legacy-queue", errorQueuePath: "errors", description: "d");
                // An int in the second position does not convert to string, so only the new overload applies.
                sb.AddQueueReceiver<SecondCommand>("stated-positional-queue", 2);
                // Named, with every remaining parameter left at its default — the one-knob call site.
                sb.AddQueueReceiver<SecondCommand>("stated-named-queue", maxConcurrentCalls: 3);

                // Same reasoning one position later for the topic-shaped pair.
                sb.AddTopicSubscription<FirstEvent>("legacy-topic", "legacy-sub", transactionMode: TransactionMode.ReceiveOnly);
                sb.AddTopicSubscription<FirstEvent>("errors-topic", "errors-positional-sub", "errors");
                sb.AddTopicSubscription<SecondEvent>("stated-topic", "stated-positional-sub", 4);
                sb.AddTopicSubscription<SecondEvent>("stated-named-topic", "stated-named-sub", maxConcurrentCalls: 5);

                // The fully-positional original shape: the trailing 10 is an int that cannot convert to
                // TransactionMode?, so the new overload is not applicable and this still binds the original.
                sb.AddSessionQueueReceiver<FirstCommand>("legacy-session-queue", null, null, null, 10);
                sb.AddSessionQueueReceiver<SecondCommand>("stated-session-queue", 6);

                sb.AddSessionTopicSubscription<FirstEvent>("legacy-session-topic", "legacy-session-sub");
                sb.AddSessionTopicSubscription<SecondEvent>("stated-session-topic", "stated-session-sub", 8);
            }).BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();

            // The global really is 7, so each stated value below is an override rather than a coincidence.
            provider.GetRequiredService<ServiceBusOptions>().MaxConcurrentCalls.Should().Be(7);

            StatedReceiver(discoveredRegistry, "legacy-queue").MaxConcurrentCalls.Should().Be(7);
            StatedReceiver(discoveredRegistry, "errors-positional-queue").MaxConcurrentCalls.Should().Be(7);
            StatedReceiver(discoveredRegistry, "named-legacy-queue").MaxConcurrentCalls.Should().Be(7);
            StatedReceiver(discoveredRegistry, "stated-positional-queue").MaxConcurrentCalls.Should().Be(2);
            StatedReceiver(discoveredRegistry, "stated-named-queue").MaxConcurrentCalls.Should().Be(3);

            StatedReceiver(discoveredRegistry, "legacy-sub").MaxConcurrentCalls.Should().Be(7);
            StatedReceiver(discoveredRegistry, "errors-positional-sub").MaxConcurrentCalls.Should().Be(7);
            StatedReceiver(discoveredRegistry, "stated-positional-sub").MaxConcurrentCalls.Should().Be(4);
            StatedReceiver(discoveredRegistry, "stated-named-sub").MaxConcurrentCalls.Should().Be(5);

            StatedReceiver(discoveredRegistry, "legacy-session-queue").MaxConcurrentCalls.Should().Be(7);
            StatedReceiver(discoveredRegistry, "stated-session-queue").MaxConcurrentCalls.Should().Be(6);

            StatedReceiver(discoveredRegistry, "legacy-session-sub").MaxConcurrentCalls.Should().Be(7);
            StatedReceiver(discoveredRegistry, "stated-session-sub").MaxConcurrentCalls.Should().Be(8);
        }

        [Fact]
        public void MustThrowWhenOneReceiverPathIsRegisteredWithTwoDifferentStatedValues()
        {
            // Two registrations of the SAME canonical receiving path stating DIFFERENT values have no defensible
            // resolution, so registration fails loudly naming the path and both values — the same posture the
            // cross-entity single-top-level-entity guard takes rather than silently picking one.
            Action build = () => BuildServices(sb =>
            {
                sb.AddQueueReceiver<FirstCommand>("dup-queue", 3);
                sb.AddQueueReceiver<SecondCommand>("dup-queue", 5);
            });

            build.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*MaxConcurrentCalls*")
                .WithMessage("*dup-queue*")
                .WithMessage("*3*")
                .WithMessage("*5*");
        }

        [Fact]
        public void MustThrowWhenAStatedMaxConcurrentCallsIsBelowOne()
        {
            // A STATED per-receiver value is rejected at REGISTRATION, where the call site that stated it is still
            // in view. The INHERITED global path is NOT validated here: core receiver init stays the single sink
            // for the at-least-1 floor.
            Action build = () => BuildServices(sb =>
                sb.AddSessionQueueReceiver<FirstCommand>("session-queue", 0));

            build.Should()
                .Throw<ArgumentOutOfRangeException>()
                .And.ParamName.Should().Be("maxConcurrentCalls");
        }

        [Fact]
        public void MustAnswerTheFirstNonNullStatedValueWhenANullEntryWasAppendedFirst()
        {
            // The discovery fold appends a DUPLICATE registry entry for a receiver it already saw, carrying a null
            // stated value. A positional first-match lookup would let such a null MASK the value the receiver
            // actually stated, so the lookup answers the first NON-NULL value instead of the first entry. Driven
            // straight at the registry because the masking order is a registry-internal ordering, not something a
            // single host build can arrange.
            var registry = new global::Chatter.MessageBrokers.AzureServiceBus.DependencyInjection.ServiceBusReceiverRegistry();
            registry.Register("dup-queue", "dup-queue", null);
            registry.Register("dup-queue", "dup-queue", null, maxConcurrentCalls: 4);

            registry.StatedMaxConcurrentCalls("dup-queue", "dup-queue").Should().Be(4);
        }

        [Fact]
        public void MustNotTreatEqualOrUnstatedValuesAsAConflict()
        {
            // Equal stated values agree and a null states nothing, so neither can conflict. Only two DIFFERENT
            // stated values are unresolvable — which is what the throw is reserved for.
            var registry = new global::Chatter.MessageBrokers.AzureServiceBus.DependencyInjection.ServiceBusReceiverRegistry();

            Action registerAgreeingEntries = () =>
            {
                registry.Register("agreeing-queue", "agreeing-queue", null, maxConcurrentCalls: 4);
                registry.Register("agreeing-queue", "agreeing-queue", null, maxConcurrentCalls: 4);
                registry.Register("agreeing-queue", "agreeing-queue", null);
            };

            registerAgreeingEntries.Should().NotThrow();
            registry.StatedMaxConcurrentCalls("agreeing-queue", "agreeing-queue").Should().Be(4);
        }

        // The live ReceiverOptions instance a named receiver path resolved to — the SAME instance
        // BrokeredMessageReceiver reads MaxConcurrentCalls from at init.
        private static ReceiverOptions StatedReceiver(IDiscoveredReceiverRegistry discoveredRegistry, string messageReceiverPath)
            => discoveredRegistry.DiscoveredReceivers.Single(r => r.MessageReceiverPath == messageReceiverPath);

        // ----------------------------------------------------------------- default-infrastructure resolution (multi-broker)

        // A non-ASB IMessagingInfrastructure whose Type is a distinct key. Registering this BEFORE AddAzureServiceBus
        // makes it the core MessagingInfrastructureProvider's default (_default = infrastructures.FirstOrDefault()),
        // so a blank-InfrastructureType receiver resolves to THIS broker, not ASB. Only the Type is exercised by
        // these DI-time guard tests; the factory members are never invoked, so they are left unimplemented.
        private sealed class PriorInfrastructure : IMessagingInfrastructure
        {
            public const string PriorInfrastructureType = "prior-broker-test";

            public string Type => PriorInfrastructureType;

            public IMessagingInfrastructureReceiver ReceiveInfrastructure
                => throw new NotImplementedException();

            public IMessagingInfrastructureDispatcher DispatchInfrastructure
                => throw new NotImplementedException();

            public IBrokeredMessagePathBuilder PathBuilder
                => throw new NotImplementedException();
        }

        // Builds services where a non-ASB IMessagingInfrastructure is registered (via the core message-broker seam)
        // BEFORE AddAzureServiceBus runs, plus core-route receivers. The prior infrastructure is added inside the
        // AddMessageBrokers(configureReceivers) delegate, which the harness invokes before AddAzureServiceBus, so at
        // PopulateFromDiscoveredReceivers time an earlier IMessagingInfrastructure descriptor already exists and ASB
        // is NOT the resolved default — exactly the multi-broker ordering the default-resolution guard depends on.
        private static ServiceCollection BuildServicesWithPriorInfrastructureAndCoreReceivers(
            Action<ServiceBusOptionsBuilder> configureServiceBus,
            Action<MessageBrokerOptionsBuilder> configureReceivers)
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddChatterCqrs(EmptyConfig(), typeof(WhenConfiguringCrossEntityTransactions))
                    .AddMessageBrokers(mb =>
                    {
                        mb.Services.AddSingleton<IMessagingInfrastructure>(new PriorInfrastructure());
                        configureReceivers(mb);
                    })
                    .AddAzureServiceBus(sb =>
                    {
                        sb.WithConnectionString(_connectionString);
                        configureServiceBus(sb);
                    });

            return services;
        }

        [Fact]
        public async Task MustNotFoldBlankTypedReceiverIntoGuardWhenAnotherInfrastructureIsDefault()
        {
            // (a) A non-ASB broker is registered FIRST, so the core resolves blank-InfrastructureType receivers to
            // IT, not ASB. A blank-typed receiver on a 2nd distinct top-level entity (under cross-entity-effective
            // mode via global FullAtomicity) must NOT be folded into ASB's single-top-level-entity guard: only the
            // single ASB-typed receiver counts, so the client is constructed and the guard does NOT throw.
            await using var provider = BuildServicesWithPriorInfrastructureAndCoreReceivers(
                sb => { },
                mb =>
                {
                    mb.WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure);
                    mb.AddReceiver<FirstCommand>("asb-queue-a", senderPath: "asb-queue-a", infrastructureType: ASBMessageContext.InfrastructureType);
                    mb.AddReceiver<SecondCommand>("other-queue-b", senderPath: "other-queue-b");
                }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should().NotThrow();
            resolveClient().Should().NotBeNull();
        }

        [Fact]
        public async Task MustNotStampMaxConcurrentCallsOntoBlankTypedReceiverWhenAnotherInfrastructureIsDefault()
        {
            // (a) The non-ASB-default blank-typed receiver is also NOT stamped with ASB's global MaxConcurrentCalls:
            // with WithMaxConcurrentCalls(7) configured, the ASB-typed receiver is stamped to 7 but the blank-typed
            // (other-broker) receiver keeps the default 1 — proving the stamp is gated by ASB-default resolution.
            var services = BuildServicesWithPriorInfrastructureAndCoreReceivers(
                sb => sb.WithMaxConcurrentCalls(7),
                mb =>
                {
                    mb.AddReceiver<FirstCommand>("asb-queue-a", senderPath: "asb-queue-a", infrastructureType: ASBMessageContext.InfrastructureType);
                    mb.AddReceiver<SecondCommand>("other-queue-b", senderPath: "other-queue-b");
                });
            await using var provider = services.BuildServiceProvider();

            // The blank receiver's 1 is also what a registry read that found NOTHING would leave behind, so pin the
            // read: it succeeded, and the ASB-typed receiver was folded — the exclusion below is a per-receiver
            // attribution decision, not a whole-loop no-op.
            var discoveredRegistry = ReadDiscoveredRegistryOffDescriptors(services);
            discoveredRegistry.Should().NotBeNull();
            discoveredRegistry.Should().BeSameAs(provider.GetRequiredService<IDiscoveredReceiverRegistry>());

            var asbRegistry = ResolveAsbReceiverRegistry(provider);
            asbRegistry.DistinctTopLevelEntities().Should().Contain("asb-queue-a");
            asbRegistry.DistinctTopLevelEntities().Should().NotContain("other-queue-b");

            var asbReceiver = discoveredRegistry.DiscoveredReceivers.Single(r => r.MessageReceiverPath == "asb-queue-a");
            var blankReceiver = discoveredRegistry.DiscoveredReceivers.Single(r => r.MessageReceiverPath == "other-queue-b");

            asbReceiver.MaxConcurrentCalls.Should()
                .Be(provider.GetRequiredService<ServiceBusOptions>().MaxConcurrentCalls);
            asbReceiver.MaxConcurrentCalls.Should().Be(7);
            blankReceiver.MaxConcurrentCalls.Should().Be(1);
        }

        [Fact]
        public async Task MustStillFoldBlankTypedReceiverIntoGuardWhenAsbIsSoleInfrastructure()
        {
            // (b) Regression: when ASB is the SOLE/first infrastructure (no prior broker), a blank-InfrastructureType
            // receiver IS still claimed — two blank-typed receivers on distinct top-level entities under cross-entity
            // -effective mode trip the guard exactly as before, proving single-broker fold-in behavior is preserved.
            await using var provider = BuildServicesWithCoreReceivers(
                sb => { },
                mb =>
                {
                    mb.WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure);
                    mb.AddReceiver<FirstCommand>("blank-queue-a", senderPath: "blank-queue-a");
                    mb.AddReceiver<SecondCommand>("blank-queue-b", senderPath: "blank-queue-b");
                }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*single top-level receiver entity*")
                .WithMessage("*blank-queue-a*")
                .WithMessage("*blank-queue-b*");
        }

        [Fact]
        public async Task MustAlwaysFoldExplicitlyAsbTypedReceiverEvenWhenAnotherInfrastructureIsDefault()
        {
            // Edge: a receiver EXPLICITLY typed to ASB is always claimed regardless of which broker is the default.
            // With a prior non-ASB infrastructure registered first, two explicitly-ASB-typed receivers on distinct
            // top-level entities under cross-entity-effective mode still trip ASB's guard — explicit typing overrides
            // the blank-default resolution.
            await using var provider = BuildServicesWithPriorInfrastructureAndCoreReceivers(
                sb => sb.WithCrossEntityTransactions(),
                mb =>
                {
                    mb.AddReceiver<FirstCommand>("asb-queue-a", senderPath: "asb-queue-a", infrastructureType: ASBMessageContext.InfrastructureType);
                    mb.AddReceiver<SecondCommand>("asb-queue-b", senderPath: "asb-queue-b", infrastructureType: ASBMessageContext.InfrastructureType);
                }).BuildServiceProvider();

            var resolveClient = () => provider.GetRequiredService<ServiceBusClient>();

            resolveClient.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*single top-level receiver entity*")
                .WithMessage("*asb-queue-a*")
                .WithMessage("*asb-queue-b*");
        }

        private sealed class FirstEvent : CQRS.Events.IEvent { }
        private sealed class SecondEvent : CQRS.Events.IEvent { }
    }
}
