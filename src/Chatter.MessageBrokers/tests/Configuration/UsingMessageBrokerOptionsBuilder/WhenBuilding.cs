using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.MessageBrokers.Reliability.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Configuration.UsingMessageBrokerOptionsBuilder
{
    public class WhenBuilding : Testing.Core.Context
    {
        [Fact]
        public void MustDefaultTransactionModeToReceiveOnly()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services).Build();

            options.TransactionMode.Should().Be(TransactionMode.ReceiveOnly);
        }

        [Fact]
        public void MustBuildNonNullReliabilityOptions()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services).Build();

            options.Reliability.Should().NotBeNull();
        }

        [Fact]
        public void MustBuildNonNullRecoveryOptions()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services).Build();

            options.Recovery.Should().NotBeNull();
        }

        [Fact]
        public void MustReflectWithTransactionMode()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure)
                .Build();

            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
        }

        [Fact]
        public void MustBuildNonNullReliabilityOptionsWhenAddReliabilityOptionsActionProvided()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .AddReliabilityOptions(r => r.WithOutboxRouting())
                .Build();

            options.Reliability.Should().NotBeNull();
            options.Reliability.RouteMessagesToOutbox.Should().BeTrue();
        }

        [Fact]
        public void MustBuildNonNullReliabilityOptionsWhenAddReliabilityOptionsActionIsNull()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .AddReliabilityOptions(null)
                .Build();

            options.Reliability.Should().NotBeNull();
        }

        [Fact]
        public void MustBuildNonNullRecoveryOptionsWhenAddRecoveryOptionsActionProvided()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(r => r.WithMaxRetryAttempts(9))
                .Build();

            options.Recovery.Should().NotBeNull();
            options.Recovery.MaxRetryAttempts.Should().Be(9);
        }

        [Fact]
        public void MustBuildNonNullRecoveryOptionsWhenAddRecoveryOptionsActionIsNull()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(null)
                .Build();

            options.Recovery.Should().NotBeNull();
        }

        [Fact]
        public void MustNotRegisterAnyServiceWhenResolved()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.Create(services)
                .WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure)
                .AddReliabilityOptions(r => r.WithOutboxRouting())
                .AddRecoveryOptions(r => r.WithMaxRetryAttempts(9))
                .Resolve();

            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            options.Reliability.RouteMessagesToOutbox.Should().BeTrue();
            options.Recovery.MaxRetryAttempts.Should().Be(9);
            options.Recovery.CircuitBreakerOptions.Should().NotBeNull();
            services.Should().BeEmpty();
        }

        [Fact]
        public void MustNotRegisterNestedOptionsBeforeBuildWhenFluentNestedOptionsUsed()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddReliabilityOptions(r => r.WithOutboxRouting())
                .AddRecoveryOptions(r => r.WithMaxRetryAttempts(9));

            services.Any(d => d.ServiceType == typeof(ReliabilityOptions)).Should().BeFalse();
            services.Any(d => d.ServiceType == typeof(RecoveryOptions)).Should().BeFalse();
            services.Any(d => d.ServiceType == typeof(CircuitBreakerOptions)).Should().BeFalse();
        }

        /// <summary>
        /// Deferring the nested publish to the parent must not lose the sub-builder's OTHER registration. The
        /// exception predicates configured through the nested builders are registered by their own publish step, so
        /// composing them through this builder still has to reach it.
        /// </summary>
        [Fact]
        public void MustRegisterRetryExceptionPredicatesProviderWhenRetryWhenUsedThroughAddRecoveryOptions()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(r => r.RetryWhen<InvalidOperationException>())
                .Build();

            services.Any(d => d.ServiceType == typeof(IRetryExceptionPredicatesProvider)).Should().BeTrue();
        }

        /// <summary>
        /// A second AddRecoveryOptions call must not silently discard the first call's predicates. Consumption is
        /// enumerable - RetryExceptionEvaluator takes every IRetryExceptionPredicatesProvider - so both configured
        /// exception types have to stay retryable.
        /// </summary>
        [Fact]
        public void MustKeepBothRetryPredicateSetsWhenAddRecoveryOptionsUsedTwice()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(r => r.RetryWhen<InvalidOperationException>())
                .AddRecoveryOptions(r => r.RetryWhen<FormatException>())
                .Build();

            using var provider = services.BuildServiceProvider();
            var evaluator = new RetryExceptionEvaluator(provider.GetServices<IRetryExceptionPredicatesProvider>());

            evaluator.ShouldRetry(new InvalidOperationException()).Should().BeTrue();
            evaluator.ShouldRetry(new FormatException()).Should().BeTrue();
        }

        [Fact]
        public void MustRegisterCircuitBreakerExceptionPredicatesProviderWhenIsTrippedByUsedThroughAddRecoveryOptions()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.Create(services)
                .AddRecoveryOptions(r => r.WithCircuitBreaker(cb => cb.IsTrippedBy<InvalidOperationException>()))
                .Build();

            services.Any(d => d.ServiceType == typeof(ICircuitBreakerExceptionPredicatesProvider)).Should().BeTrue();
        }

        [Fact]
        public void MustHonourConfiguredTransactionModeWhenStaticFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration();

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            AssertConfiguredTransactionModeHonoured(options);
        }

        [Fact]
        public void MustHonourConfiguredTransactionModeWhenInstanceFromConfigForwardsServicesAndConfiguration()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration();
            var builder = new MessageBrokerOptionsBuilder(services, configuration);

            var options = builder.FromConfig();

            AssertConfiguredTransactionModeHonoured(options);
        }

        [Fact]
        public void MustHonourNumericConfiguredTransactionModeWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = ((byte)TransactionMode.FullAtomicityViaInfrastructure).ToString()
                })
                .Build();

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
        }

        [Fact]
        public void MustRetainEveryFluentDefaultWhenFromConfigSectionPresentWithoutChildKeys()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [MessageBrokerOptionsBuilder.MessageBrokerSectionName] = string.Empty
                })
                .Build();
            configuration.GetSection(MessageBrokerOptionsBuilder.MessageBrokerSectionName).Exists().Should().BeTrue();

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            options.TransactionMode.Should().Be(TransactionMode.ReceiveOnly);
            options.Reliability.RouteMessagesToOutbox.Should().BeFalse();
            options.Reliability.MinutesToLiveInMemory.Should().Be(10);
            options.Reliability.EnableOutboxPollingProcessor.Should().BeFalse();
            options.Reliability.OutboxProcessingIntervalInMilliseconds.Should().Be(5000);
            options.Recovery.MaxRetryAttempts.Should().Be(5);
            options.Recovery.CircuitBreakerOptions.NumberOfFailuresBeforeOpen.Should().Be(5);
        }

        [Fact]
        public void MustHonourNestedReliabilitySectionWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:RouteMessagesToOutbox"] = "true"
                })
                .Build();

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            options.Reliability.RouteMessagesToOutbox.Should().BeTrue();
        }

        [Fact]
        public void MustHonourNestedRecoverySectionWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts"] = "42"
                })
                .Build();

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            options.Recovery.MaxRetryAttempts.Should().Be(42);
        }

        [Fact]
        public void MustHonourNestedCircuitBreakerSectionWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen"] = "9"
                })
                .Build();

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            options.Recovery.CircuitBreakerOptions.NumberOfFailuresBeforeOpen.Should().Be(9);
        }

        [Fact]
        public void MustLeaveNestedOptionsResolvableWhenFromConfigNestedSectionsPopulated()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{ReliabilityOptionsBuilder.ReliabilityOptionsSectionName}:RouteMessagesToOutbox"] = "true",
                    [$"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts"] = "42",
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:NumberOfFailuresBeforeOpen"] = "9"
                })
                .Build();

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ReliabilityOptions>().Should().BeSameAs(options.Reliability);
            provider.GetRequiredService<RecoveryOptions>().Should().BeSameAs(options.Recovery);
            provider.GetRequiredService<CircuitBreakerOptions>().Should().BeSameAs(options.Recovery.CircuitBreakerOptions);
        }

        [Fact]
        public void MustRegisterExactlyOneMessageBrokerOptionsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration();

            MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            services.Count(d => d.ServiceType == typeof(MessageBrokerOptions)).Should().Be(1);
        }

        [Fact]
        public void MustRetainFluentTransactionModeWhenInstanceFromConfigSectionOmitsIt()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{RecoveryOptionsBuilder.RecoveryOptionsSectionName}:MaxRetryAttempts"] = "42"
                })
                .Build();
            var builder = new MessageBrokerOptionsBuilder(services, configuration);

            var options = builder.WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure).FromConfig();

            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            options.Recovery.MaxRetryAttempts.Should().Be(42);
        }

        [Fact]
        public void MustRegisterExactlyOneSingletonPerOptionsTypeWhenInstanceFromConfigFollowsFluentNestedOptions()
        {
            var services = new ServiceCollection();
            var configuration = BuildConfiguration();
            var builder = new MessageBrokerOptionsBuilder(services, configuration);
            builder.AddReliabilityOptions(r => r.WithOutboxRouting());
            builder.AddRecoveryOptions(r => r.WithMaxRetryAttempts(9));

            var options = builder.FromConfig();

            options.Reliability.RouteMessagesToOutbox.Should().BeTrue();
            options.Recovery.MaxRetryAttempts.Should().Be(9);
            services.Count(d => d.ServiceType == typeof(MessageBrokerOptions)).Should().Be(1);
            services.Count(d => d.ServiceType == typeof(ReliabilityOptions)).Should().Be(1);
            services.Count(d => d.ServiceType == typeof(RecoveryOptions)).Should().Be(1);
            services.Count(d => d.ServiceType == typeof(CircuitBreakerOptions)).Should().Be(1);
        }

        [Fact]
        public void MustPreferExplicitSectionOverDefaultSectionWhenBothAreAvailable()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = nameof(TransactionMode.None),
                    [$"{ExplicitSectionName}:TransactionMode"] = nameof(TransactionMode.FullAtomicityViaInfrastructure)
                })
                .Build();
            var builder = new MessageBrokerOptionsBuilder(services, configuration, configuration.GetSection(ExplicitSectionName));

            var options = builder.Build();

            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.FromConfig(services, BuildConfiguration());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<MessageBrokerOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<MessageBrokerOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsSnapshotWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.FromConfig(services, BuildConfiguration());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsSnapshot<MessageBrokerOptions>>().Value
                .Should().BeSameAs(provider.GetRequiredService<MessageBrokerOptions>());
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromIOptionsMonitorWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            MessageBrokerOptionsBuilder.FromConfig(services, BuildConfiguration());

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptionsMonitor<MessageBrokerOptions>>().CurrentValue
                .Should().BeSameAs(provider.GetRequiredService<MessageBrokerOptions>());
        }

        [Fact]
        public void MustDefaultTransactionModeToReceiveOnlyOnEveryOptionsFacetWhenNoConfigurationIsPresent()
        {
            var services = new ServiceCollection();
            // AddOptions() stands in for the host, which registers the open generic options descriptors. Without the
            // built options registered against the closed generics, those descriptors are what every facet resolves
            // through - and the instance they hand out carries TransactionMode None, so a receive that fails after the
            // message is taken loses it.
            services.AddOptions();

            MessageBrokerOptionsBuilder.Create(services).Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<MessageBrokerOptions>>().Value
                .TransactionMode.Should().Be(TransactionMode.ReceiveOnly);
            provider.GetRequiredService<IOptionsSnapshot<MessageBrokerOptions>>().Value
                .TransactionMode.Should().Be(TransactionMode.ReceiveOnly);
            provider.GetRequiredService<IOptionsMonitor<MessageBrokerOptions>>().CurrentValue
                .TransactionMode.Should().Be(TransactionMode.ReceiveOnly);
        }

        [Fact]
        public void MustResolveTheBuiltOptionsFromEveryOptionsFacetWhenNoConfigurationIsPresent()
        {
            var services = new ServiceCollection();
            services.AddOptions();

            var options = MessageBrokerOptionsBuilder.Create(services).Build();

            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IOptions<MessageBrokerOptions>>().Value.Should().BeSameAs(options);
            provider.GetRequiredService<IOptionsSnapshot<MessageBrokerOptions>>().Value.Should().BeSameAs(options);
            provider.GetRequiredService<IOptionsMonitor<MessageBrokerOptions>>().CurrentValue.Should().BeSameAs(options);
            provider.GetRequiredService<MessageBrokerOptions>().Should().BeSameAs(options);
        }

        [Fact]
        public void MustRetainTheBuiltNestedOptionsOnTheOptionsFacetWhenFromConfigSectionPopulated()
        {
            var services = new ServiceCollection();

            var options = MessageBrokerOptionsBuilder.FromConfig(services, BuildConfiguration());

            using var provider = services.BuildServiceProvider();
            var facetOptions = provider.GetRequiredService<IOptions<MessageBrokerOptions>>().Value;

            facetOptions.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            facetOptions.Reliability.Should().BeSameAs(options.Reliability);
            facetOptions.Recovery.Should().BeSameAs(options.Recovery);
            facetOptions.Recovery.CircuitBreakerOptions.Should().BeSameAs(options.Recovery.CircuitBreakerOptions);
        }

        /// <summary>
        /// The JSON configuration provider carries an explicit null through as a section with no value and no
        /// children, so binding one must leave the seeded nested instance in place rather than replace it with null.
        /// The publish step dereferences the finalized graph - RecoveryOptionsBuilder.Publish reads
        /// RecoveryOptions.CircuitBreakerOptions, and AddBuiltOptions registers each nested instance as a singleton -
        /// so a nulled child would fail registration outright instead of falling back to the fluent default.
        /// </summary>
        [Fact]
        public void MustPublishTheSeededNestedOptionsWhenFromConfigNestedSectionsAreExplicitJsonNulls()
        {
            var services = new ServiceCollection();
            var configuration = BuildJsonConfiguration(
                @"{ ""Chatter"": { ""MessageBrokers"": { ""TransactionMode"": ""FullAtomicityViaInfrastructure"", ""Reliability"": null, ""Recovery"": null } } }");

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            options.Reliability.Should().NotBeNull();
            options.Recovery.Should().NotBeNull();
            options.Recovery.CircuitBreakerOptions.Should().NotBeNull();

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ReliabilityOptions>().Should().BeSameAs(options.Reliability);
            provider.GetRequiredService<RecoveryOptions>().Should().BeSameAs(options.Recovery);
            provider.GetRequiredService<CircuitBreakerOptions>().Should().BeSameAs(options.Recovery.CircuitBreakerOptions);
        }

        /// <summary>
        /// The same guarantee one level deeper, through the documented 'CircuitBreaker' key that
        /// RecoveryOptions.CircuitBreakerOptions is aliased onto - the only spelling the binder reaches. An explicit
        /// null there must not null the grandchild RecoveryOptionsBuilder.Publish dereferences. The bound
        /// MaxRetryAttempts pins that the surrounding section really was applied, so the surviving circuit breaker
        /// default is not just an unbound section.
        /// </summary>
        [Fact]
        public void MustPublishTheSeededCircuitBreakerOptionsWhenFromConfigCircuitBreakerSectionIsAnExplicitJsonNull()
        {
            var services = new ServiceCollection();
            var configuration = BuildJsonConfiguration(
                @"{ ""Chatter"": { ""MessageBrokers"": { ""Recovery"": { ""MaxRetryAttempts"": 42, ""CircuitBreaker"": null } } } }");

            var options = MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            options.Recovery.MaxRetryAttempts.Should().Be(42);
            options.Recovery.CircuitBreakerOptions.Should().NotBeNull();
            options.Recovery.CircuitBreakerOptions.NumberOfFailuresBeforeOpen.Should().Be(5);

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<RecoveryOptions>().Should().BeSameAs(options.Recovery);
            provider.GetRequiredService<CircuitBreakerOptions>().Should().BeSameAs(options.Recovery.CircuitBreakerOptions);
        }

        private const string ExplicitSectionName = "Custom:MessageBrokers";

        private static void AssertConfiguredTransactionModeHonoured(MessageBrokerOptions options)
        {
            options.Should().NotBeNull();
            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            options.Reliability.Should().NotBeNull();
            options.Recovery.Should().NotBeNull();
        }

        private static IConfiguration BuildJsonConfiguration(string json)
            => new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                .Build();

        private static IConfiguration BuildConfiguration()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = nameof(TransactionMode.FullAtomicityViaInfrastructure)
                })
                .Build();
    }
}
