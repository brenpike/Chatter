using Chatter.MessageBrokers.AzureServiceBus.DependencyInjection;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
// Disambiguate the local ServiceBusReceiver (system under test) from the SDK type of the same name
// pulled in by `using Azure.Messaging.ServiceBus;` (CS0104).
using ServiceBusReceiver = Chatter.MessageBrokers.AzureServiceBus.Receiving.ServiceBusReceiver;
using ServiceBusClient = Azure.Messaging.ServiceBus.ServiceBusClient;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingServiceBusReceiver
{
    // Pins ServiceBusReceiver.CreateProductionReceiver's session-vs-non-session branch. This uses the
    // PRODUCTION receiver factory (no test receiverFactory seam supplied) so the real branch runs, and
    // observes the chosen adapter directly through the internal InnerReceiver accessor. Neither adapter
    // ctor opens a connection (the non-session adapter lazily creates its SDK receiver only on first
    // ReceiveAsync; the session adapter accepts a session only on ReceiveAsync), so resolving InnerReceiver
    // is connection-free and the concrete adapter TYPE is the observable proof of the registry branch.
    public class WhenSelectingProductionReceiver : Testing.Core.Context
    {
        private const string _connectionString =
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret";

        // A placeholder SAS connection string opens no connection (the SDK connects lazily), so this is a
        // valid stand-in for the DI-provided shared client.
        private static ServiceBusClient CreateClient() => new ServiceBusClient(_connectionString);

        private static IBodyConverterFactory JsonFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        // Constructs the SUT on the PRODUCTION receiver factory (the five-argument ctor leaves
        // receiverFactory null, so CreateProductionReceiver is used) with the supplied registry.
        private static async Task<ServiceBusReceiver> InitializedSutAsync(ServiceBusReceiverRegistry registry, string receiverPath)
        {
            var serviceBusOptions = new ServiceBusOptions { ConnectionString = _connectionString };
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            var sut = new ServiceBusReceiver(CreateClient(), serviceBusOptions, new MessageBrokerOptions(), logger.Object, JsonFactory(), registry);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = receiverPath, SendingPath = receiverPath }, CancellationToken.None);
            return sut;
        }

        [Fact]
        public async Task MustSelectSessionAdapterWhenRegistryMarksEntitySessionMode()
        {
            var registry = new ServiceBusReceiverRegistry();
            registry.Register("session-queue", transactionMode: null, requiresSession: true);
            var sut = await InitializedSutAsync(registry, "session-queue");

            sut.InnerReceiver.Should().BeOfType<AzureSdkSessionMessageReceiverAdapter>();
        }

        [Fact]
        public async Task MustSelectNonSessionAdapterWhenRegistryEntryIsNotSessionMode()
        {
            var registry = new ServiceBusReceiverRegistry();
            registry.Register("plain-queue", transactionMode: null, requiresSession: false);
            var sut = await InitializedSutAsync(registry, "plain-queue");

            sut.InnerReceiver.Should().BeOfType<AzureSdkMessageReceiverAdapter>();
        }

        [Fact]
        public async Task MustSelectNonSessionAdapterWhenRegistryHasNoMatchingEntity()
        {
            var registry = new ServiceBusReceiverRegistry();
            registry.Register("some-other-queue", transactionMode: null, requiresSession: true);
            var sut = await InitializedSutAsync(registry, "plain-queue");

            sut.InnerReceiver.Should().BeOfType<AzureSdkMessageReceiverAdapter>();
        }

        [Fact]
        public async Task MustSelectNonSessionAdapterWhenRegistryAbsent()
        {
            // The five-argument production ctor allows a null registry (existing callers/tests); the
            // session-vs-non-session branch null-guards it and selects the non-session adapter.
            var sut = await InitializedSutAsync(registry: null, receiverPath: "plain-queue");

            sut.InnerReceiver.Should().BeOfType<AzureSdkMessageReceiverAdapter>();
        }
    }
}
