using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Reliability.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
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
        public void MustThrowNamingInvalidCircuitBreakerOptionWhenNestedSectionCarriesInvalidValue()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName}:ConcurrentHalfOpenAttempts"] = "0"
                })
                .Build();

            var fromConfig = () => MessageBrokerOptionsBuilder.FromConfig(services, configuration);

            fromConfig.Should().Throw<CircuitBreakerOptionsValidationException>()
                .WithMessage($"*{nameof(CircuitBreakerOptions.ConcurrentHalfOpenAttempts)}*");
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

        private static void AssertConfiguredTransactionModeHonoured(MessageBrokerOptions options)
        {
            options.Should().NotBeNull();
            options.TransactionMode.Should().Be(TransactionMode.FullAtomicityViaInfrastructure);
            options.Reliability.Should().NotBeNull();
            options.Recovery.Should().NotBeNull();
        }

        private static IConfiguration BuildConfiguration()
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    [$"{MessageBrokerOptionsBuilder.MessageBrokerSectionName}:TransactionMode"] = nameof(TransactionMode.FullAtomicityViaInfrastructure)
                })
                .Build();
    }
}
