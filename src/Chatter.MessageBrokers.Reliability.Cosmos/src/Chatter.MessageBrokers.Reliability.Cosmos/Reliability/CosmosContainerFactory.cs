using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// Derives (never provisions) per-registration Cosmos <see cref="Container"/> handles from the app-registered
    /// <see cref="CosmosClient"/> singleton and caches them. By default a handle is derived via
    /// <c>client.GetContainer(database, containerName)</c>; when a registration supplies an explicit factory the handle
    /// comes from that factory instead. The provider creates no Cosmos resources — the application owns the
    /// <see cref="CosmosClient"/> lifecycle and the existence of the database/container (ADR-0008).
    /// </summary>
    public sealed class CosmosContainerFactory
    {
        private readonly IServiceProvider _serviceProvider;

        // INVARIANT: cache keyed by (database, containerName). Many command types may map to one container, and distinct
        // in-flight command types resolve their containers concurrently on the pipeline, so the cache must be
        // thread-safe. The value is a Lazy<Container> with ExecutionAndPublication: ConcurrentDictionary.GetOrAdd does
        // NOT guarantee its value factory runs only once under a concurrent miss for the same key, so caching the
        // Container directly could invoke Derive (and an app-supplied explicit factory) more than once. Wrapping in a
        // single-execution Lazy guarantees Derive runs EXACTLY once per cache key even when several threads race the
        // same miss — only one redundant Lazy wrapper is discarded by the dictionary, never a second Derive.
        private readonly ConcurrentDictionary<string, Lazy<Container>> _cache = new ConcurrentDictionary<string, Lazy<Container>>(StringComparer.Ordinal);

        public CosmosContainerFactory(IServiceProvider serviceProvider)
            => _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        /// <summary>
        /// Returns the document (aggregate) container for <paramref name="registration"/>, derived from the app's
        /// <see cref="CosmosClient"/> (or the registration's explicit document factory) and cached per
        /// (database, container name).
        /// </summary>
        public Container GetDocumentContainer(DocumentReliabilityRegistration registration)
        {
            _ = registration ?? throw new ArgumentNullException(nameof(registration));
            return GetOrAdd(registration.Database, registration.ContainerName, registration.DocumentContainerFactory);
        }

        /// <summary>
        /// Returns the change-feed lease container for <paramref name="registration"/>, derived from the app's
        /// <see cref="CosmosClient"/> (or the registration's explicit lease factory) and cached per (database, lease name).
        /// </summary>
        public Container GetLeaseContainer(DocumentReliabilityRegistration registration)
        {
            _ = registration ?? throw new ArgumentNullException(nameof(registration));
            return GetOrAdd(registration.Database, registration.LeaseName, registration.LeaseContainerFactory);
        }

        private Container GetOrAdd(string database, string containerName, Func<IServiceProvider, Container> explicitFactory)
        {
            var key = database + "\0" + containerName;
            return _cache.GetOrAdd(
                key,
                _ => new Lazy<Container>(() => Derive(database, containerName, explicitFactory), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private Container Derive(string database, string containerName, Func<IServiceProvider, Container> explicitFactory)
        {
            if (explicitFactory is not null)
            {
                return explicitFactory(_serviceProvider)
                    ?? throw new InvalidOperationException(
                        $"The explicit container factory for database '{database}' container '{containerName}' returned null.");
            }

            // GetRequiredService so a missing app-registered CosmosClient fails loudly with a clear message rather than
            // a null-ref deep in container derivation. The app owns the CosmosClient registration (a documented
            // prerequisite); the provider registers none.
            CosmosClient client = _serviceProvider.GetService<CosmosClient>()
                ?? throw new InvalidOperationException(
                    "No CosmosClient is registered in the service provider. WithCosmosDocumentReliability requires the application to register a CosmosClient singleton (the application owns its lifecycle); the provider creates no client and provisions no Cosmos resources.");

            return client.GetContainer(database, containerName);
        }
    }
}
