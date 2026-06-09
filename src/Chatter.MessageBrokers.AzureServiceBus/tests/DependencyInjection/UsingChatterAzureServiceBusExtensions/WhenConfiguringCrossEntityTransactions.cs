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
        // config binding (ServiceBusOptionsBuilder.UseConfig -> section.Get<ServiceBusOptions>()), with NO
        // fluent WithCrossEntityTransactions() call.
        private static IConfiguration CrossEntityConfig(bool enableCrossEntityTransactions)
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Chatter:Infrastructure:AzureServiceBus:ConnectionString"] = _connectionString,
                ["Chatter:Infrastructure:AzureServiceBus:EnableCrossEntityTransactions"] =
                    enableCrossEntityTransactions.ToString(),
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
            await using var provider = BuildServicesWithCoreReceivers(
                sb => sb.WithMaxConcurrentCalls(7),
                mb => mb.AddReceiver<FirstCommand>("core-queue-a", senderPath: "core-queue-a", infrastructureType: ASBMessageContext.InfrastructureType))
                .BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();
            var asbReceiver = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "core-queue-a");

            asbReceiver.MaxConcurrentCalls.Should().Be(7);
        }

        [Fact]
        public async Task MustLeaveDiscoveredAsbReceiverMaxConcurrentCallsAtDefaultWhenGlobalUnset()
        {
            // Zero-behavior-change guard: with the global MaxConcurrentCalls unset (default 1), the stamp leaves
            // each ASB receiver's effective MaxConcurrentCalls at the default 1 — proving the flow does not alter
            // existing single-call (sequential) receive behavior for hosts that never configure it.
            await using var provider = BuildServicesWithCoreReceivers(
                sb => { },
                mb => mb.AddReceiver<FirstCommand>("core-queue-a", senderPath: "core-queue-a", infrastructureType: ASBMessageContext.InfrastructureType))
                .BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();
            var asbReceiver = discoveredRegistry.DiscoveredReceivers
                .Single(r => r.MessageReceiverPath == "core-queue-a");

            asbReceiver.MaxConcurrentCalls.Should().Be(1);
        }

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
            await using var provider = BuildServicesWithPriorInfrastructureAndCoreReceivers(
                sb => sb.WithMaxConcurrentCalls(7),
                mb =>
                {
                    mb.AddReceiver<FirstCommand>("asb-queue-a", senderPath: "asb-queue-a", infrastructureType: ASBMessageContext.InfrastructureType);
                    mb.AddReceiver<SecondCommand>("other-queue-b", senderPath: "other-queue-b");
                }).BuildServiceProvider();

            var discoveredRegistry = provider.GetRequiredService<IDiscoveredReceiverRegistry>();
            var asbReceiver = discoveredRegistry.DiscoveredReceivers.Single(r => r.MessageReceiverPath == "asb-queue-a");
            var blankReceiver = discoveredRegistry.DiscoveredReceivers.Single(r => r.MessageReceiverPath == "other-queue-b");

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
