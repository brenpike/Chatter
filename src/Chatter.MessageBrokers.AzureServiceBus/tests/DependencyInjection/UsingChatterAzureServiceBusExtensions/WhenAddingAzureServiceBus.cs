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
using ServiceBusSender = Azure.Messaging.ServiceBus.ServiceBusSender;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.DependencyInjection.UsingChatterAzureServiceBusExtensions
{
    // Characterization tests pinning the OBSERVABLE wiring contract of AddAzureServiceBus: building the
    // minimal Chatter + AddMessageBrokers + AddAzureServiceBus registration, then resolving
    // IMessagingInfrastructure and asserting the resolved CLR types and per-call behavior of its members.
    //
    // These assert observable behavior (the resolved IMessagingInfrastructure surface — Type, the
    // ServiceBusReceiver/ServiceBusMessageSender concrete types produced by ReceiveInfrastructure/
    // DispatchInfrastructure, the per-Create fresh-instance semantics, and the
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
        // string. AddLogging is required because the receiver factory delegate resolves a
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
            // Pins the per-Create behavior: ServiceBusReceiver is TRANSIENT, so each ReceiveInfrastructure
            // access resolves a fresh instance and two accesses yield DISTINCT instances. This matters
            // because InitializeAsync writes the entity's options onto the instance and GetReceiver runs
            // once per receiver entity — a shared instance would cross-wire entities.
            await using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            var first = infrastructure.ReceiveInfrastructure;
            var second = infrastructure.ReceiveInfrastructure;

            first.Should().NotBeSameAs(second);
        }

        [Fact]
        public async Task MustYieldDistinctDispatcherInstancePerDispatchInfrastructureAccess()
        {
            // Same fresh-instance-per-Create behavior for the dispatcher factory delegate.
            await using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            var first = infrastructure.DispatchInfrastructure;
            var second = infrastructure.DispatchInfrastructure;

            first.Should().NotBeSameAs(second);
        }

        [Fact]
        public async Task MustYieldUndisposedReceiverFromReceiveInfrastructure()
        {
            // The receiver handed to a caller must be usable. A registration factory that resolved it
            // inside a DI scope it then disposed would hand back an already-disposed receiver, because the
            // scope disposes every disposable it created as it closes.
            await using var provider = BuildProvider();

            var infrastructure = provider.GetRequiredService<IMessagingInfrastructure>();

            var receiver = (ServiceBusReceiver)infrastructure.ReceiveInfrastructure;
            receiver.IsDisposed.Should().BeFalse();
        }

        [Fact]
        public async Task MustNotDisposeAScopedDependencyOfTheReceiverOnReceiveInfrastructureAccess()
        {
            // Pins "the registration factory opens NO scope" by construction, one level below the receiver
            // itself: a SCOPED disposable standing in for a receiver dependency can only be disposed by a
            // scope that both created it and closed. Root-scope resolution never disposes it before
            // provider teardown.
            var probes = new List<ProbeBodyConverterFactory>();
            var services = BuildServices();
            services.AddScoped<IBodyConverterFactory>(_ => TrackProbe(probes));

            await using var provider = services.BuildServiceProvider();

            _ = provider.GetRequiredService<IMessagingInfrastructure>().ReceiveInfrastructure;

            probes.Should().NotBeEmpty("the probe must actually sit on the receiver's resolution path");
            probes.Should().NotContain(probe => probe.IsDisposed);
        }

        [Fact]
        public async Task MustNotDisposeAScopedDependencyOfTheDispatcherOnDispatchInfrastructureAccess()
        {
            // The dispatcher sibling of the receiver probe above: the sender's factory dependency, made
            // scoped and disposable, must survive a DispatchInfrastructure access.
            var probes = new List<ProbeServiceBusMessageSenderFactory>();
            var services = BuildServices();
            services.AddScoped<IServiceBusMessageSenderFactory>(_ => TrackProbe(probes));

            await using var provider = services.BuildServiceProvider();

            _ = provider.GetRequiredService<IMessagingInfrastructure>().DispatchInfrastructure;

            probes.Should().NotBeEmpty("the probe must actually sit on the dispatcher's resolution path");
            probes.Should().NotContain(probe => probe.IsDisposed);
        }

        [Fact]
        public void MustRegisterServiceBusReceiverAsTransient()
        {
            // InitializeAsync writes per-entity state onto the receiver and GetReceiver is called once per
            // receiver entity, so a shared instance would cross-wire entities; and a SCOPED instance handed
            // out by a singleton factory would outlive its scope. Transient is the only correct lifetime.
            var services = BuildServices();

            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ServiceBusReceiver));

            descriptor.Should().NotBeNull();
            descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
        }

        [Fact]
        public void MustRegisterServiceBusMessageSenderAsTransient()
        {
            var services = BuildServices();

            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ServiceBusMessageSender));

            descriptor.Should().NotBeNull();
            descriptor.Lifetime.Should().Be(ServiceLifetime.Transient);
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

        // Records every probe the container constructs so the test can inspect instances it never resolves
        // itself — the instance created inside a factory-opened scope would otherwise be unreachable.
        private static TProbe TrackProbe<TProbe>(List<TProbe> probes) where TProbe : new()
        {
            var probe = new TProbe();
            probes.Add(probe);
            return probe;
        }

        // Stands in for the receiver's IBodyConverterFactory dependency. Never invoked: these tests only
        // resolve the receiver, they never convert a message body.
        private sealed class ProbeBodyConverterFactory : IBodyConverterFactory, System.IDisposable
        {
            public bool IsDisposed { get; private set; }

            public IBrokeredMessageBodyConverter CreateBodyConverter(string contentType) => null;

            public void Dispose() => IsDisposed = true;
        }

        // Stands in for the sender's IServiceBusMessageSenderFactory dependency. Never invoked: these tests
        // only resolve the sender, they never dispatch.
        private sealed class ProbeServiceBusMessageSenderFactory : IServiceBusMessageSenderFactory, System.IDisposable
        {
            public bool IsDisposed { get; private set; }

            public ServiceBusSender Create(string destinationEntityPath) => null;

            public void Dispose() => IsDisposed = true;
        }
    }
}
