#nullable disable

using Chatter.CQRS;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Reliability.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Chatter.MessageBrokers.Tests.DependencyInjection.UsingChatterMessageBrokerExtensions
{
    /// <summary>
    /// Verifies the documented DI entry point actually reads the <c>Chatter:MessageBrokers</c> section.
    /// <c>AddMessageBrokerOptions</c> hands the builder an <see cref="IConfiguration"/> but no
    /// <see cref="IConfigurationSection"/>, so the section that gets bound has to be resolved by the builder
    /// itself; without that resolution every key under <c>Chatter:MessageBrokers</c> is silently discarded for
    /// a consumer configuring Chatter through <c>AddMessageBrokers</c>.
    /// </summary>
    public class WhenBindingMessageBrokerConfiguration : Testing.Core.Context
    {
        [Fact]
        public void MustHonourConfiguredTransactionModeWhenReachedThroughAddMessageBrokerOptions()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration(new Dictionary<string, string>
            {
                [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = nameof(TransactionMode.FullAtomicityViaInfrastructure)
            });

            var options = services.AddMessageBrokerOptions(configuration).Build();

            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
        }

        [Fact]
        public void MustDefaultTransactionModeToReceiveOnlyWhenConfigurationCarriesNoBrokerSection()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder().Build();

            var options = services.AddMessageBrokerOptions(configuration).Build();

            options.TransactionMode.Should().Be(TransactionMode.ReceiveOnly);
        }

        [Fact]
        public void MustHonourNestedRecoveryKeyWhenReachedThroughAddMessageBrokerOptions()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration(new Dictionary<string, string>
            {
                [$"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts"] = "42"
            });

            var options = services.AddMessageBrokerOptions(configuration).Build();

            options.Recovery.MaxRetryAttempts.Should().Be(42);
        }

        [Fact]
        public void MustHonourConfiguredTransactionModeOnEveryOptionsFacetWhenReachedThroughAddMessageBrokerOptions()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration(new Dictionary<string, string>
            {
                [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = nameof(TransactionMode.FullAtomicityViaInfrastructure)
            });

            services.AddMessageBrokerOptions(configuration).Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<MessageBrokerOptions>>().Value
                .TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            provider.GetRequiredService<IOptionsSnapshot<MessageBrokerOptions>>().Value
                .TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            provider.GetRequiredService<IOptionsMonitor<MessageBrokerOptions>>().CurrentValue
                .TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
        }

        [Fact]
        public void MustHonourConfiguredTransactionModeOnEveryOptionsFacetWhenReachedThroughAddMessageBrokers()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration(new Dictionary<string, string>
            {
                [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = nameof(TransactionMode.FullAtomicityViaInfrastructure)
            });

            services.AddChatterCqrs(configuration, NoBrokeredMessageAssembly)
                .AddMessageBrokers(receiverHandlerSourceBuilder: b => b.WithExplicitAssemblies(NoBrokeredMessageAssembly));

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<MessageBrokerOptions>>().Value
                .TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            provider.GetRequiredService<IOptionsSnapshot<MessageBrokerOptions>>().Value
                .TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            provider.GetRequiredService<IOptionsMonitor<MessageBrokerOptions>>().CurrentValue
                .TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromEveryFacetOfEveryOptionsTypeWhenAddOptionsRanFirst()
        {
            var services = new ServiceCollection();
            services.AddOptions();

            var builtOptions = services.AddMessageBrokerOptions(BuildPopulatedConfiguration()).Build();

            AssertConfiguredValuesLanded(builtOptions);
            AssertEveryFacetOfEveryOptionsTypeResolvesTheBuiltOptions(services, builtOptions);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromEveryFacetOfEveryOptionsTypeWhenAddOptionsRanLast()
        {
            var services = new ServiceCollection();

            var builtOptions = services.AddMessageBrokerOptions(BuildPopulatedConfiguration()).Build();
            services.AddOptions();

            AssertConfiguredValuesLanded(builtOptions);
            AssertEveryFacetOfEveryOptionsTypeResolvesTheBuiltOptions(services, builtOptions);
        }

        // The CQRS marker assembly carries no [BrokeredMessage]-decorated IMessage types, so attribute-driven receiver
        // discovery is deterministically empty and AddMessageBrokers contributes options registrations only.
        private static readonly Assembly NoBrokeredMessageAssembly = typeof(IMessage).Assembly;

        // INVARIANT: one options instance, everywhere. Every accessor of every options type in the graph has to hand
        // back the very instance Build() produced; a second instance anywhere is a set of values nothing
        // seeded with the fluent defaults.
        private static void AssertEveryFacetOfEveryOptionsTypeResolvesTheBuiltOptions(IServiceCollection services, MessageBrokerOptions builtOptions)
        {
            using var provider = services.BuildServiceProvider();

            AssertEveryFacetResolves(provider, builtOptions);
            AssertEveryFacetResolves(provider, builtOptions.Reliability);
            AssertEveryFacetResolves(provider, builtOptions.Recovery);
            AssertEveryFacetResolves(provider, builtOptions.Recovery.CircuitBreakerOptions);
        }

        private static void AssertEveryFacetResolves<TOptions>(IServiceProvider provider, TOptions builtOptions) where TOptions : class
        {
            provider.GetRequiredService<IOptions<TOptions>>().Value.Should().BeSameAs(builtOptions);
            provider.GetRequiredService<IOptionsSnapshot<TOptions>>().Value.Should().BeSameAs(builtOptions);
            provider.GetRequiredService<IOptionsMonitor<TOptions>>().CurrentValue.Should().BeSameAs(builtOptions);
            provider.GetRequiredService<TOptions>().Should().BeSameAs(builtOptions);
        }

        private static void AssertConfiguredValuesLanded(MessageBrokerOptions builtOptions)
        {
            builtOptions.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            builtOptions.Reliability.RouteMessagesToOutbox.Should().BeTrue();
            builtOptions.Recovery.MaxRetryAttempts.Should().Be(42);
            builtOptions.Recovery.CircuitBreakerOptions.NumberOfFailuresBeforeOpen.Should().Be(9);
        }

        private static IConfiguration BuildPopulatedConfiguration()
            => BuildConfiguration(new Dictionary<string, string>
            {
                [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = nameof(TransactionMode.FullAtomicityViaInfrastructure),
                [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:RouteMessagesToOutbox"] = "true",
                [$"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts"] = "42",
                [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen"] = "9"
            });

        private static IConfiguration BuildConfiguration(Dictionary<string, string> values)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
    }
}
