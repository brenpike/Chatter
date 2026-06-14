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
        // receiverFactory null, so CreateProductionReceiver is used) with the supplied registry. sendingPath
        // defaults to receiverPath (the queue-receiver shape); a subscription supplies its topic explicitly so
        // the per-receiver session key carries (subscription, topic).
        private static async Task<ServiceBusReceiver> InitializedSutAsync(ServiceBusReceiverRegistry registry, string receiverPath, string sendingPath = null)
        {
            var serviceBusOptions = new ServiceBusOptions { ConnectionString = _connectionString };
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            var sut = new ServiceBusReceiver(CreateClient(), serviceBusOptions, new MessageBrokerOptions(), logger.Object, JsonFactory(), registry);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = receiverPath, SendingPath = sendingPath ?? receiverPath }, CancellationToken.None);
            return sut;
        }

        // The formatted subscription path core BrokeredMessageReceiver.StartReceiver rewrites
        // options.MessageReceiverPath into (line 261:
        // PathBuilder.GetMessageReceivingPath(SendingPath, MessageReceiverPath)) BEFORE the ASB receiver factory
        // runs its RequiresSession lookup. Reproducing that rewrite here is what makes these tests exercise the
        // runtime lookup path: the registry is registered with the RAW subscription name, but the production
        // factory is handed this FORMATTED path as MessageReceiverPath — the exact raw-vs-formatted mismatch the
        // canonical key closes.
        private const string SubscriptionsSegment = "Subscriptions";
        private static string FormattedSubscriptionPath(string topic, string subscription)
            => $"{topic}/{SubscriptionsSegment}/{subscription}";

        [Fact]
        public async Task MustSelectSessionAdapterWhenRegistryMarksEntitySessionMode()
        {
            var registry = new ServiceBusReceiverRegistry();
            registry.Register("session-queue", "session-queue", transactionMode: null, requiresSession: true);
            var sut = await InitializedSutAsync(registry, "session-queue");

            sut.InnerReceiver.Should().BeOfType<AzureSdkSessionMessageReceiverAdapter>();
        }

        [Fact]
        public async Task MustSelectNonSessionAdapterWhenRegistryEntryIsNotSessionMode()
        {
            var registry = new ServiceBusReceiverRegistry();
            registry.Register("plain-queue", "plain-queue", transactionMode: null, requiresSession: false);
            var sut = await InitializedSutAsync(registry, "plain-queue");

            sut.InnerReceiver.Should().BeOfType<AzureSdkMessageReceiverAdapter>();
        }

        [Fact]
        public async Task MustSelectNonSessionAdapterWhenRegistryHasNoMatchingEntity()
        {
            var registry = new ServiceBusReceiverRegistry();
            registry.Register("some-other-queue", "some-other-queue", transactionMode: null, requiresSession: true);
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

        [Fact]
        public async Task MustResolveSessionAdapterForTopicSubscriptionRegisteredRawAfterRuntimeFormattedRewrite()
        {
            // The exact iter-2 P1 failing case. A topic session subscription is REGISTERED with the raw
            // subscription name (as AddSessionTopicSubscription does: Register(topic, sub, ...)), but at runtime
            // core StartReceiver rewrites MessageReceiverPath to the FORMATTED "<topic>/Subscriptions/<sub>"
            // BEFORE CreateProductionReceiver's RequiresSession lookup. The canonical-key derivation must map the
            // formatted lookup back to the raw registration so the session adapter is selected. Before STEP-001
            // the formatted path failed the lookup and fell through to the non-session adapter, so this test
            // would have FAILED.
            const string topic = "orders-topic";
            const string subscription = "orders-session-sub";
            var registry = new ServiceBusReceiverRegistry();
            registry.Register(topic, subscription, transactionMode: null, requiresSession: true);

            var sut = await InitializedSutAsync(registry, receiverPath: FormattedSubscriptionPath(topic, subscription), sendingPath: topic);

            sut.InnerReceiver.Should().BeOfType<AzureSdkSessionMessageReceiverAdapter>();
        }

        [Fact]
        public async Task MustRouteEachSubscriptionOnSharedTopicToItsOwnAdapterAtRuntimeFormattedPaths()
        {
            // The exact P2 collision case, exercised through the RUNTIME formatted path. A session subscription
            // and a normal subscription on the SAME topic are registered with their raw subscription names, but
            // each arrives at the lookup with its formatted "<topic>/Subscriptions/<sub>" path (StartReceiver's
            // rewrite). The session flag is keyed PER-RECEIVER (subscription + topic), so the session
            // subscription routes to the session adapter while the normal subscription on the same topic routes
            // to the non-session adapter — they no longer collide on the shared top-level entity (the topic).
            const string sharedTopic = "shared-topic";
            var registry = new ServiceBusReceiverRegistry();
            registry.Register(sharedTopic, "session-subscription", transactionMode: null, requiresSession: true);
            registry.Register(sharedTopic, "normal-subscription", transactionMode: null, requiresSession: false);

            var sessionSut = await InitializedSutAsync(registry, receiverPath: FormattedSubscriptionPath(sharedTopic, "session-subscription"), sendingPath: sharedTopic);
            var normalSut = await InitializedSutAsync(registry, receiverPath: FormattedSubscriptionPath(sharedTopic, "normal-subscription"), sendingPath: sharedTopic);

            sessionSut.InnerReceiver.Should().BeOfType<AzureSdkSessionMessageReceiverAdapter>();
            normalSut.InnerReceiver.Should().BeOfType<AzureSdkMessageReceiverAdapter>();
        }

        [Fact]
        public async Task MustSelectSessionAdapterForQueueReceiverWithNoRuntimeRewrite()
        {
            // Queue session receivers have no formatted rewrite: SendingPath == MessageReceiverPath, so
            // StartReceiver's GetMessageReceivingPath returns the raw queue name unchanged. The raw registration
            // and the (unchanged) runtime path must still resolve to the session adapter.
            const string sessionQueue = "session-queue";
            var registry = new ServiceBusReceiverRegistry();
            registry.Register(sessionQueue, sessionQueue, transactionMode: null, requiresSession: true);

            var sut = await InitializedSutAsync(registry, receiverPath: sessionQueue);

            sut.InnerReceiver.Should().BeOfType<AzureSdkSessionMessageReceiverAdapter>();
        }

        [Fact]
        public async Task MustResolveSessionAdapterWithoutDoubleFormattingWhenLookupPathAlreadyFormatted()
        {
            // Idempotency guard. The canonical-key derivation must NOT re-format an already-formatted
            // subscription path into "<topic>/Subscriptions/<topic>/Subscriptions/<sub>". Feeding the
            // already-formatted runtime path must collapse to the SAME canonical key the raw registration
            // produced, so the session adapter is still selected. A double-format would miss the registration and
            // fall through to the non-session adapter.
            const string topic = "events-topic";
            const string subscription = "events-session-sub";
            var registry = new ServiceBusReceiverRegistry();
            registry.Register(topic, subscription, transactionMode: null, requiresSession: true);

            var sut = await InitializedSutAsync(registry, receiverPath: FormattedSubscriptionPath(topic, subscription), sendingPath: topic);

            sut.InnerReceiver.Should().BeOfType<AzureSdkSessionMessageReceiverAdapter>();
        }
    }
}
