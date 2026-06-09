using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using ServiceBusClient = Azure.Messaging.ServiceBus.ServiceBusClient;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.DependencyInjection.UsingChatterAzureServiceBusExtensions
{
    // Characterization tests pinning the OBSERVABLE wiring contract of AddAzureServiceBus: building the
    // minimal Chatter + AddMessageBrokers + AddAzureServiceBus registration, then resolving
    // IMessagingInfrastructure and asserting the resolved CLR types and per-call behavior of its members.
    //
    // These assert observable behavior (the resolved IMessagingInfrastructure surface — Type, the
    // ServiceBusReceiver/ServiceBusMessageSender concrete types produced by ReceiveInfrastructure/
    // DispatchInfrastructure, the per-Create fresh-scope instance semantics, and the
    // AzureServiceBusEntityPathBuilder behind PathBuilder) — NOT the name of the concrete infrastructure
    // factory type. They therefore survive the STEP-005 rewire from the per-broker
    // ServiceBusInfrastructureFactory to the shared core factory unchanged.
    //
    // Full resolution (not the ServiceDescriptor fallback) is used: nothing connects to Azure Service Bus
    // at registration or resolution time. ServiceBusReceiver's ctor only parses the connection string
    // (ServiceBusConnectionStringBuilder), and ServiceBusMessageSender is lazy (no connection until first
    // SendAsync), so a placeholder SAS connection string is sufficient and no live broker is required.
    public class WhenAddingAzureServiceBus : Testing.Core.Context
    {
        private const string _connectionString =
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret";

        private static IConfiguration EmptyConfig()
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

        // Builds the minimal Chatter + AzureServiceBus registration path against a placeholder connection
        // string. AddLogging is required because the receiver factory delegate resolves a scoped
        // ServiceBusReceiver, which depends on ILogger<ServiceBusReceiver>.
        private static ServiceProvider BuildProvider()
            => BuildServices().BuildServiceProvider();

        private static ServiceCollection BuildServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddChatterCqrs(EmptyConfig(), typeof(WhenAddingAzureServiceBus))
                    .AddMessageBrokers()
                    .AddAzureServiceBus(o => o.WithConnectionString(_connectionString));

            return services;
        }

        [Fact]
        public async Task MustResolveMessagingInfrastructureWithAzureServiceBusType()
        {
            await using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            infrastructure.Should().NotBeNull();
            infrastructure.Type.Should().Be(ASBMessageContext.InfrastructureType);
        }

        [Fact]
        public async Task MustResolveReceiveInfrastructureAsServiceBusReceiver()
        {
            await using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            IMessagingInfrastructureReceiver receiver = infrastructure.ReceiveInfrastructure;
            receiver.Should().BeOfType<ServiceBusReceiver>();
        }

        [Fact]
        public async Task MustResolveDispatchInfrastructureAsServiceBusMessageSender()
        {
            await using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            IMessagingInfrastructureDispatcher dispatcher = infrastructure.DispatchInfrastructure;
            dispatcher.Should().BeOfType<ServiceBusMessageSender>();
        }

        [Fact]
        public async Task MustYieldDistinctReceiverInstancePerReceiveInfrastructureAccess()
        {
            // Pins the documented per-Create behavior: each ReceiveInfrastructure access opens a fresh DI
            // scope, resolves the scoped ServiceBusReceiver, and disposes the scope — so the scoped
            // instance intentionally outlives the resolution scope and two accesses yield DISTINCT
            // instances.
            await using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            var first = infrastructure.ReceiveInfrastructure;
            var second = infrastructure.ReceiveInfrastructure;

            first.Should().NotBeSameAs(second);
        }

        [Fact]
        public async Task MustYieldDistinctDispatcherInstancePerDispatchInfrastructureAccess()
        {
            // Same fresh-scope-per-Create behavior for the dispatcher factory delegate.
            await using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            var first = infrastructure.DispatchInfrastructure;
            var second = infrastructure.DispatchInfrastructure;

            first.Should().NotBeSameAs(second);
        }

        [Fact]
        public async Task MustRegisterSharedServiceBusClientAsSingleton()
        {
            // Cross-entity transactions require ONE ServiceBusClient per namespace, so the client is
            // registered as a singleton built once from ServiceBusOptions.
            var services = BuildServices();

            var clientDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ServiceBusClient));

            clientDescriptor.Should().NotBeNull();
            clientDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);

            await using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<ServiceBusClient>().Should().NotBeNull();
        }

        [Fact]
        public async Task MustResolveSenderFactoryAsAzureSdkMessageSenderFactory()
        {
            await using var provider = BuildProvider();

            var senderFactory = provider.GetRequiredService<IServiceBusMessageSenderFactory>();

            senderFactory.Should().BeOfType<AzureSdkMessageSenderFactory>();
        }

        [Fact]
        public async Task MustResolvePathBuilderAsAzureServiceBusEntityPathBuilder()
        {
            // Pins the 4-arg MessagingInfrastructure ctor path: PathBuilder is the ASB-specific
            // AzureServiceBusEntityPathBuilder, not the core DefaultBrokeredMessagePathBuilder.
            await using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            infrastructure.PathBuilder.Should().BeOfType<AzureServiceBusEntityPathBuilder>();
        }
    }
}
