using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // Boots Chatter's REAL DI graph (AddChatterCqrs + AddMessageBrokers) against the live emulator and drives the
    // document-tier reliability pipeline entirely through Chatter public contracts:
    //   - the app CosmosClient (from CosmosTestClient) is registered as the singleton the provider derives handles from;
    //   - a single CapturingInfrastructure is the only IMessagingInfrastructure, so it is the default the relay's
    //     GetDispatcher resolves and the broker sink the suite asserts on;
    //   - per-test WithCosmosDocumentReliability<TCommand>(...) registrations + test handlers are layered via the
    //     pipeline/services hooks;
    //   - delivery is driven through IReceivedMessageDispatcher.DispatchAsync<TCommand>(payload, MessageBrokerContext)
    //     so the FULL receive pipeline runs with a non-null InboundBrokeredMessage carrying the MessageId (the
    //     document-tier behavior opens a batch only on that seam);
    //   - the REAL CosmosOutboxRelayHostedService is exposed via Start/StopAsync.
    //
    // The provider is async-disposed so the registered CosmosClient (owned by CosmosTestClient, disposed separately by
    // the test) and any IAsyncDisposable services tear down cleanly.
    public sealed class CosmosReliabilityHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IReadOnlyList<IHostedService> _hostedServices;
        private bool _started;

        private CosmosReliabilityHarness(ServiceProvider provider, IReadOnlyList<IHostedService> hostedServices, CapturingInfrastructure capture)
        {
            _provider = provider;
            _hostedServices = hostedServices;
            Capture = capture;
        }

        // The capture sink the suite asserts published broker messages against.
        public CapturingInfrastructure Capture { get; }

        // Builds the harness. configurePipeline layers the per-test WithCosmosDocumentReliability<TCommand>(...)
        // registrations onto Chatter's CommandPipelineBuilder; configureServices (optional) registers the test handlers
        // (and any singletons they share with the test) into the real graph AFTER Chatter wiring and BEFORE the
        // provider is built, so Chatter's dispatcher resolves them on the receive path.
        public static CosmosReliabilityHarness Build(
            CosmosClient cosmosClient,
            Action<CommandPipelineBuilder> configurePipeline,
            Action<IServiceCollection> configureServices)
        {
            _ = cosmosClient ?? throw new ArgumentNullException(nameof(cosmosClient));
            _ = configurePipeline ?? throw new ArgumentNullException(nameof(configurePipeline));

            var configuration = new ConfigurationBuilder().Build();
            var services = new ServiceCollection();

            // A bare ServiceCollection has no logging; the Chatter receiver/relay graph depends on ILogger<T>, so add
            // it up front (a real host would register it automatically).
            services.AddLogging();

            var capture = new CapturingInfrastructure();

            // Point the handler/receiver assembly scan at THIS test assembly (via a marker type in it) so concrete test
            // handlers registered below resolve, mirroring the Azure Service Bus integration harness.
            var testAssembly = typeof(CosmosReliabilityHarness).Assembly;

            services
                .AddChatterCqrs(configuration, configurePipeline, typeof(CosmosReliabilityHarness))
                .AddMessageBrokers(receiverAssemblies: testAssembly);

            // The app owns the CosmosClient (the provider registers none); register the emulator-provisioned client as
            // the singleton the CosmosContainerFactory derives every container handle from.
            services.AddSingleton(cosmosClient);

            // The ONLY IMessagingInfrastructure: it is the default the relay's GetDispatcher resolves and the broker
            // sink the suite captures publications on.
            services.AddSingleton<IMessagingInfrastructure>(capture);

            configureServices?.Invoke(services);

            var provider = services.BuildServiceProvider();
            var hostedServices = new List<IHostedService>(provider.GetServices<IHostedService>());

            return new CosmosReliabilityHarness(provider, hostedServices, capture);
        }

        // Delivers payload through the FULL receive pipeline: IReceivedMessageDispatcher creates a scope, runs the
        // command pipeline (including the document-tier batch-lifecycle behavior) with a non-null
        // InboundBrokeredMessage carrying messageId. The capturing infrastructure type is stamped on the inbound
        // application properties so a follow-up Send the handler issues reconstructs an outbound carrying it (and the
        // relay's GetDispatcher resolves the capture sink by name as well as default).
        public Task DeliverAsync<TCommand>(string messageId, TCommand payload, string receiverPath, IDictionary<string, object> applicationProperties = null, CancellationToken cancellationToken = default)
            where TCommand : class, IMessage
        {
            var dispatcher = _provider.GetRequiredService<IReceivedMessageDispatcher>();
            var bodyConverter = new JsonBodyConverter();
            byte[] body = bodyConverter.Convert(payload);

            var properties = new Dictionary<string, object>(applicationProperties ?? new Dictionary<string, object>())
            {
                [MessageContext.InfrastructureType] = CapturingInfrastructure.InfrastructureType,
            };

            var messageContext = new MessageBrokerContext(messageId, body, properties, receiverPath, cancellationToken, bodyConverter);
            return dispatcher.DispatchAsync(payload, messageContext, cancellationToken);
        }

        // Starts the real relay hosted service(s). Idempotent.
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_started)
            {
                return;
            }

            foreach (IHostedService hostedService in _hostedServices)
            {
                await hostedService.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            _started = true;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (!_started)
            {
                return;
            }

            foreach (IHostedService hostedService in _hostedServices)
            {
                await hostedService.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            _started = false;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
    }
}
