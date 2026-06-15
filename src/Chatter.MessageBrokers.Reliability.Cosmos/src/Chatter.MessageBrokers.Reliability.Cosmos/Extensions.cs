using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using Microsoft.Azure.Cosmos;
using System;
using System.Linq;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        /// <summary>
        /// Registers a per-command document-tier reliability participation for <typeparamref name="TCommand"/>. The
        /// command's aggregate, co-resident outbox doc, and inbox marker live in the container identified by
        /// <paramref name="database"/> + <paramref name="container"/>, derived (never provisioned) from the
        /// app-registered <see cref="CosmosClient"/> singleton. Participation IS having a registration (ADR-0008): a
        /// command type without a registration bypasses the document tier entirely. Callable multiple times — each call
        /// adds one registration; the singleton infrastructure (registry, container factory, surface, outbox, and the
        /// outermost batch-lifecycle behavior) is registered once and idempotently.
        /// </summary>
        /// <remarks>
        /// PREREQUISITE: the application MUST register a <see cref="CosmosClient"/> singleton in the service provider —
        /// the provider derives container handles from it and owns NO client. A duplicate registration for the same
        /// <typeparamref name="TCommand"/> throws.
        /// </remarks>
        /// <param name="database">The Cosmos database the document container lives in.</param>
        /// <param name="container">The document (aggregate) container the aggregate, outbox doc, and inbox marker are co-resident in.</param>
        /// <param name="lease">The change-feed lease container — consumed by the #222 relay's per-registration fan-out.</param>
        /// <param name="resolver">App-supplied Try/nullable delegate mapping the inbound message to its aggregate partition key.</param>
        /// <param name="partitionKeyPath">The container's partition-key path (e.g. <c>"/tenantId"</c>; hierarchical containers carry multiple segments).</param>
        public static CommandPipelineBuilder WithCosmosDocumentReliability<TCommand>(this CommandPipelineBuilder pipelineBuilder,
                                                                                     string database,
                                                                                     string container,
                                                                                     string lease,
                                                                                     ResolvePartitionKey resolver,
                                                                                     params string[] partitionKeyPath)
            where TCommand : ICommand
        {
            _ = pipelineBuilder ?? throw new ArgumentNullException(nameof(pipelineBuilder));
            if (string.IsNullOrWhiteSpace(database))
            {
                throw new ArgumentException("A non-null, non-whitespace database is required.", nameof(database));
            }
            if (string.IsNullOrWhiteSpace(container))
            {
                throw new ArgumentException("A non-null, non-whitespace container is required.", nameof(container));
            }
            if (string.IsNullOrWhiteSpace(lease))
            {
                throw new ArgumentException("A non-null, non-whitespace lease is required.", nameof(lease));
            }

            var registration = new DocumentReliabilityRegistration(
                typeof(TCommand),
                database,
                container,
                lease,
                ValidateResolver(resolver),
                ValidatePartitionKeyPath(partitionKeyPath));

            return pipelineBuilder.AddRegistration(registration);
        }

        /// <summary>
        /// Advanced overload: the application supplies explicit per-registration container factories rather than naming
        /// a database/container/lease. The <see cref="CosmosContainerFactory"/> invokes these factories instead of
        /// deriving handles via <c>client.GetContainer</c>; use this when the application resolves its containers from
        /// the service provider itself.
        /// </summary>
        public static CommandPipelineBuilder WithCosmosDocumentReliability<TCommand>(this CommandPipelineBuilder pipelineBuilder,
                                                                                     Func<IServiceProvider, Container> documentContainerFactory,
                                                                                     Func<IServiceProvider, Container> leaseContainerFactory,
                                                                                     ResolvePartitionKey resolver,
                                                                                     params string[] partitionKeyPath)
            where TCommand : ICommand
        {
            _ = pipelineBuilder ?? throw new ArgumentNullException(nameof(pipelineBuilder));
            _ = documentContainerFactory ?? throw new ArgumentNullException(nameof(documentContainerFactory));
            _ = leaseContainerFactory ?? throw new ArgumentNullException(nameof(leaseContainerFactory));

            // The explicit factories bypass GetContainer derivation, so no real database/container/lease names exist.
            // The command type's full name is used as the synthetic cache identity — one registration per command type
            // keeps the (database, containerName) cache key distinct per participant.
            var syntheticIdentity = typeof(TCommand).FullName;
            var registration = new DocumentReliabilityRegistration(
                typeof(TCommand),
                syntheticIdentity,
                syntheticIdentity + ":document",
                syntheticIdentity + ":lease",
                ValidateResolver(resolver),
                ValidatePartitionKeyPath(partitionKeyPath),
                documentContainerFactory,
                leaseContainerFactory);

            return pipelineBuilder.AddRegistration(registration);
        }

        private static ResolvePartitionKey ValidateResolver(ResolvePartitionKey resolver)
            => resolver ?? throw new ArgumentNullException(nameof(resolver));

        private static System.Collections.ObjectModel.ReadOnlyCollection<string> ValidatePartitionKeyPath(string[] partitionKeyPath)
        {
            if (partitionKeyPath is null || partitionKeyPath.Length == 0)
            {
                throw new ArgumentException("A container partition-key path is required.", nameof(partitionKeyPath));
            }

            // Clone before storing so post-registration mutation of the caller-owned array cannot corrupt the registered
            // path, and validate every segment: document writers stamp the resolved partition-key value at this declared
            // path, so an empty/whitespace segment would break the carriage contract.
            var partitionKeyPathSegments = (string[])partitionKeyPath.Clone();
            for (var i = 0; i < partitionKeyPathSegments.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(partitionKeyPathSegments[i]))
                {
                    throw new ArgumentException("Every container partition-key path segment must be non-null and non-whitespace.", nameof(partitionKeyPath));
                }
            }

            return Array.AsReadOnly(partitionKeyPathSegments);
        }

        // Adds the per-type registration to the shared singleton registry (additive; duplicate command type throws) and
        // registers the singleton infrastructure ONCE and idempotently across N WithCosmosDocumentReliability<T> calls.
        private static CommandPipelineBuilder AddRegistration(this CommandPipelineBuilder pipelineBuilder, DocumentReliabilityRegistration registration)
        {
            DocumentReliabilityRegistry registry = GetOrCreateRegistry(pipelineBuilder.Services);

            // Additive; throws (clear message) on a duplicate command-type registration.
            registry.Add(registration);

            // The Cosmos container factory derives+caches Container handles from the app-registered CosmosClient.
            pipelineBuilder.Services.AddIfNotRegistered<CosmosContainerFactory>(
                ServiceLifetime.Singleton, sp => new CosmosContainerFactory(sp));

            // The scoped document-tier reliability surface holds the per-message handle (one message -> one
            // registration, so no conflict). REPLACE (not AddIfNotRegistered) so the IDocumentTierReliabilitySurface
            // alias deterministically resolves to the SAME concrete DocumentTierReliabilitySurface the batch-lifecycle
            // behavior writes the handle onto — a pre-existing alias to a different instance would otherwise leave
            // CosmosBrokeredMessageOutbox resolving a surface the behavior never populates. Replace re-binds the
            // identical pair on every call, so it stays idempotent-in-effect across N registrations.
            pipelineBuilder.Services.Replace<DocumentTierReliabilitySurface, DocumentTierReliabilitySurface>(ServiceLifetime.Scoped);
            pipelineBuilder.Services.Replace<IDocumentTierReliabilitySurface>(
                ServiceLifetime.Scoped, sp => sp.GetRequiredService<DocumentTierReliabilitySurface>());

            // The Cosmos outbox contributes the outbox-doc create-op to the framework-owned batch via the surface
            // handle. REPLACE (not AddIfNotRegistered) is REQUIRED: AddMessageBrokers ALWAYS pre-registers a default
            // IBrokeredMessageOutbox (InMemoryBrokeredMessageOutbox) and a default IRouteBrokeredMessages, so
            // AddIfNotRegistered would be a silent no-op and SendToOutbox would bind the wrong (non-Cosmos) outbox.
            // This mirrors the EF provider's WithOutboxProcessingBehavior. The Cosmos provider deliberately does NOT
            // implement IPollableOutboxStore — dispatch is the #222 change-feed relay, not a polling query (ADR-0007).
            // Replace stays idempotent-in-effect across N calls.
            pipelineBuilder.Services.Replace<IBrokeredMessageOutbox, CosmosBrokeredMessageOutbox>(ServiceLifetime.Scoped);

            // Routing is participation-gated by a HandleGatedOutboxRouter decorator (#238, ADR-0008). The naive global
            // Replace of IRouteBrokeredMessages with OutboxBrokeredMessageRouter routes EVERY command's outbound through
            // the Cosmos outbox — but only PARTICIPANTS open a document-tier batch (which sets the surface handle), so a
            // NON-participant command whose handler Send/Publishes would hit CosmosBrokeredMessageOutbox's null-handle
            // throw, contradicting "a command type without a registration bypasses the document tier entirely". The
            // decorator gates on the surface handle: participant (handle set, mid-batch) -> Cosmos outbox router;
            // non-participant / no open batch (handle null) -> the core-registered DEFAULT router, exactly as if Cosmos
            // were not installed. Because Replace RemoveAll's first, the captured default descriptor MUST be read BEFORE
            // the Replace; the decorator factory materializes the inner default from it (honoring its lifetime and all
            // three descriptor shapes). Idempotent across N calls: a second call sees HandleGatedOutboxRouter already in
            // place and skips re-wrapping, never double-wrapping (which would re-route participant dispatch through an
            // extra decorator and break the "inner is the core default" contract). The decorator is registered via the
            // factory Replace overload, so its descriptor's ImplementationType is null; idempotency is therefore tracked
            // by a dedicated marker type (HandleGatedRouterMarker) registered once on first wrap — mirroring the
            // once-only behavior guard below, which uses descriptor presence rather than the router's own shape.
            bool decoratorAlreadyWired = pipelineBuilder.Services
                .Any(descriptor => descriptor.ServiceType == typeof(HandleGatedRouterMarker));

            if (!decoratorAlreadyWired)
            {
                ServiceDescriptor capturedDefaultRouter = pipelineBuilder.Services
                    .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IRouteBrokeredMessages));

                pipelineBuilder.Services.Replace<IRouteBrokeredMessages>(
                    ServiceLifetime.Scoped,
                    sp => new HandleGatedOutboxRouter(
                        new OutboxBrokeredMessageRouter(sp.GetRequiredService<IBrokeredMessageOutbox>()),
                        MaterializeDefaultRouter(sp, capturedDefaultRouter),
                        sp.GetRequiredService<IDocumentTierReliabilitySurface>()));

                pipelineBuilder.Services.AddSingleton(new HandleGatedRouterMarker());
            }

            // OUTERMOST: register the open-generic behavior EXACTLY ONCE (first WithBehavior = outermost via the
            // CommandBehaviorPipeline reverse). Only the per-type registration above is additive.
            if (!pipelineBuilder.Services.Any(descriptor =>
                    descriptor.ServiceType == typeof(ICommandBehavior<>)
                    && descriptor.ImplementationType == typeof(DocumentTierBatchLifecycleBehavior<>)))
            {
                pipelineBuilder.WithBehavior(typeof(DocumentTierBatchLifecycleBehavior<>));
            }

            return pipelineBuilder;
        }

        // Materializes the core-registered default IRouteBrokeredMessages from the descriptor captured BEFORE the Replace
        // RemoveAll'd it, honoring all three descriptor shapes (ImplementationType / ImplementationFactory /
        // ImplementationInstance). The capture happens at registration time but a missing descriptor only surfaces here,
        // inside the factory, so a clear named error fires instead of silently routing non-participants to the Cosmos
        // outbox (core ALWAYS registers a default IRouteBrokeredMessages, so this should be unreachable).
        private static IRouteBrokeredMessages MaterializeDefaultRouter(IServiceProvider serviceProvider, ServiceDescriptor capturedDefaultRouter)
        {
            if (capturedDefaultRouter is null)
            {
                throw new InvalidOperationException(
                    "No default IRouteBrokeredMessages was registered before WithCosmosDocumentReliability ran. The Cosmos "
                    + "document tier decorates the core router; call AddMessageBrokers (which registers the default router) first.");
            }

            if (capturedDefaultRouter.ImplementationInstance is IRouteBrokeredMessages instance)
            {
                return instance;
            }

            if (capturedDefaultRouter.ImplementationFactory is Func<IServiceProvider, object> factory)
            {
                return (IRouteBrokeredMessages)factory(serviceProvider);
            }

            if (capturedDefaultRouter.ImplementationType is Type implementationType)
            {
                return (IRouteBrokeredMessages)ActivatorUtilities.CreateInstance(serviceProvider, implementationType);
            }

            throw new InvalidOperationException(
                "The captured default IRouteBrokeredMessages descriptor carries no implementation type, factory, or instance.");
        }

        // Resolves the single shared registry singleton instance (so additive registrations accumulate in ONE registry),
        // creating and registering it on first call.
        private static DocumentReliabilityRegistry GetOrCreateRegistry(IServiceCollection services)
        {
            ServiceDescriptor existing = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(DocumentReliabilityRegistry));
            if (existing?.ImplementationInstance is DocumentReliabilityRegistry registry)
            {
                return registry;
            }

            registry = new DocumentReliabilityRegistry();
            services.AddSingleton(registry);
            return registry;
        }
    }
}
