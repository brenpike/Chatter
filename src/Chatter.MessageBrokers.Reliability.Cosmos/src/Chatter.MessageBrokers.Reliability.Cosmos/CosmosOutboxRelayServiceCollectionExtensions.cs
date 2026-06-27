using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Microsoft.Extensions.Hosting;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Registers a STANDALONE Cosmos change-feed outbox relay — a hosted service draining one explicitly-configured
    /// monitored container's change feed, independent of the command-pipeline <c>WithCosmosDocumentReliability&lt;TCommand&gt;</c>
    /// registry. Use it to relay an outbox container the application owns directly rather than one selected by command-type
    /// participation.
    /// </summary>
    public static class CosmosOutboxRelayServiceCollectionExtensions
    {
        /// <summary>
        /// Registers a standalone Cosmos outbox change-feed relay configured by <paramref name="configure"/>. Each call
        /// registers a SEPARATE <see cref="IHostedService"/>, so the method is repeatable (N calls => N relays over N
        /// configured sources). It does NOT touch the <c>DocumentReliabilityRegistry</c> — a standalone relay is orthogonal
        /// to command-type participation.
        /// </summary>
        /// <remarks>
        /// PREREQUISITE: the application must register an <see cref="IMessagingInfrastructureProvider"/> and an
        /// <see cref="IBodyConverterFactory"/> (provided by <c>AddMessageBrokers</c>) so the relay can publish; the
        /// monitored + lease container factories own resolving their handles from the app-registered <c>CosmosClient</c>.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
        /// <exception cref="ArgumentException">The configured options omit a required factory or partition-key path.</exception>
        public static IServiceCollection AddCosmosOutboxRelay(this IServiceCollection services, Action<CosmosOutboxRelayOptions> configure)
        {
            _ = services ?? throw new ArgumentNullException(nameof(services));
            _ = configure ?? throw new ArgumentNullException(nameof(configure));

            var options = new CosmosOutboxRelayOptions();
            configure(options);
            ValidateOptions(options);

            // Register a distinct singleton IHostedService per call (factory-built, so the registration never dedupes): two
            // calls yield two relays. The host resolves IMessagingInfrastructureProvider + IBodyConverterFactory from DI and
            // captures the validated options.
            services.AddSingleton<IHostedService>(serviceProvider => new StandaloneCosmosOutboxRelayHostedService(
                serviceProvider,
                serviceProvider.GetRequiredService<IMessagingInfrastructureProvider>(),
                serviceProvider.GetRequiredService<IBodyConverterFactory>(),
                options));

            return services;
        }

        private static void ValidateOptions(CosmosOutboxRelayOptions options)
        {
            if (options.MonitoredContainerFactory is null)
            {
                throw new ArgumentException(
                    "A monitored-container factory is required. Set CosmosOutboxRelayOptions.MonitoredContainerFactory to resolve the container whose change feed the standalone relay drains.",
                    nameof(options.MonitoredContainerFactory));
            }

            if (options.LeaseContainerFactory is null)
            {
                throw new ArgumentException(
                    "A lease-container factory is required. Set CosmosOutboxRelayOptions.LeaseContainerFactory to resolve the lease container backing the change-feed processor.",
                    nameof(options.LeaseContainerFactory));
            }

            if (options.PartitionKeyPath is null || options.PartitionKeyPath.Count == 0)
            {
                throw new ArgumentException(
                    "A non-empty partition-key path is required. Set CosmosOutboxRelayOptions.PartitionKeyPath to the monitored container's declared partition-key path.",
                    nameof(options.PartitionKeyPath));
            }

            // Eagerly map the options into the relay's delivery settings so the F2 construction invariants (delivered status
            // non-empty and != pending, delivered TTL > 0 including the -1 "retain indefinitely" rejection, status patch path
            // anchored to the status field, ttl patch path a valid JSON pointer) throw a clear ArgumentException AT
            // REGISTRATION — before the service provider is built — instead of deferring the failure to host construction.
            // This reuses the SAME validating builder the host uses, so the checks are not duplicated here.
            _ = OutboxDeliverySettings.FromOptions(options);
        }
    }
}
