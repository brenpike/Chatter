using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
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

        private sealed class FirstEvent : CQRS.Events.IEvent { }
        private sealed class SecondEvent : CQRS.Events.IEvent { }
    }
}
