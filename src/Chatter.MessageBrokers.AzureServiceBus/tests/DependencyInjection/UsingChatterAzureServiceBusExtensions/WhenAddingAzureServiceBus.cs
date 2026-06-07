using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using Xunit;

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
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddChatterCqrs(EmptyConfig(), typeof(WhenAddingAzureServiceBus))
                    .AddMessageBrokers()
                    .AddAzureServiceBus(o => o.WithConnectionString(_connectionString));

            return services.BuildServiceProvider();
        }

        [Fact]
        public void MustResolveMessagingInfrastructureWithAzureServiceBusType()
        {
            using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            infrastructure.Should().NotBeNull();
            infrastructure.Type.Should().Be(ASBMessageContext.InfrastructureType);
        }

        [Fact]
        public void MustResolveReceiveInfrastructureAsServiceBusReceiver()
        {
            using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            IMessagingInfrastructureReceiver receiver = infrastructure.ReceiveInfrastructure;
            receiver.Should().BeOfType<ServiceBusReceiver>();
        }

        [Fact]
        public void MustResolveDispatchInfrastructureAsServiceBusMessageSender()
        {
            using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            IMessagingInfrastructureDispatcher dispatcher = infrastructure.DispatchInfrastructure;
            dispatcher.Should().BeOfType<ServiceBusMessageSender>();
        }

        [Fact]
        public void MustYieldDistinctReceiverInstancePerReceiveInfrastructureAccess()
        {
            // Pins the documented per-Create behavior: each ReceiveInfrastructure access opens a fresh DI
            // scope, resolves the scoped ServiceBusReceiver, and disposes the scope — so the scoped
            // instance intentionally outlives the resolution scope and two accesses yield DISTINCT
            // instances.
            using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            var first = infrastructure.ReceiveInfrastructure;
            var second = infrastructure.ReceiveInfrastructure;

            first.Should().NotBeSameAs(second);
        }

        [Fact]
        public void MustYieldDistinctDispatcherInstancePerDispatchInfrastructureAccess()
        {
            // Same fresh-scope-per-Create behavior for the dispatcher factory delegate.
            using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            var first = infrastructure.DispatchInfrastructure;
            var second = infrastructure.DispatchInfrastructure;

            first.Should().NotBeSameAs(second);
        }

        [Fact]
        public void MustResolvePathBuilderAsAzureServiceBusEntityPathBuilder()
        {
            // Pins the 4-arg MessagingInfrastructure ctor path: PathBuilder is the ASB-specific
            // AzureServiceBusEntityPathBuilder, not the core DefaultBrokeredMessagePathBuilder.
            using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            infrastructure.PathBuilder.Should().BeOfType<AzureServiceBusEntityPathBuilder>();
        }
    }
}
