using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Microsoft.Azure.Cosmos;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        /// <summary>
        /// Registers the Cosmos document-tier reliability surface and the outermost Document-Tier Batch-Lifecycle
        /// Behavior on the command pipeline. The application injects its document (aggregate) container and change-feed
        /// lease container as instances; the provider creates NO container (binding only).
        /// </summary>
        /// <param name="documentContainer">The container the aggregate, outbox doc, and inbox marker are co-resident in.</param>
        /// <param name="leaseContainer">The change-feed lease container — registered now, consumed by the #222 relay.</param>
        /// <param name="resolvePartitionKey">App-supplied delegate mapping the inbound message to its aggregate partition key.</param>
        /// <param name="containerPartitionKeyPath">The container's partition-key path (e.g. <c>"/tenantId"</c>; hierarchical containers carry multiple segments).</param>
        public static CommandPipelineBuilder WithCosmosDocumentReliability(this CommandPipelineBuilder pipelineBuilder,
                                                                           Container documentContainer,
                                                                           Container leaseContainer,
                                                                           ResolvePartitionKey resolvePartitionKey,
                                                                           params string[] containerPartitionKeyPath)
        {
            _ = documentContainer ?? throw new ArgumentNullException(nameof(documentContainer));
            _ = leaseContainer ?? throw new ArgumentNullException(nameof(leaseContainer));

            return pipelineBuilder.WithCosmosDocumentReliability(
                _ => documentContainer,
                _ => leaseContainer,
                resolvePartitionKey,
                containerPartitionKeyPath);
        }

        /// <summary>
        /// Factory overload of <see cref="WithCosmosDocumentReliability(CommandPipelineBuilder, Container, Container, ResolvePartitionKey, string[])"/>
        /// for applications that resolve their containers from the service provider.
        /// </summary>
        public static CommandPipelineBuilder WithCosmosDocumentReliability(this CommandPipelineBuilder pipelineBuilder,
                                                                           Func<IServiceProvider, Container> documentContainerFactory,
                                                                           Func<IServiceProvider, Container> leaseContainerFactory,
                                                                           ResolvePartitionKey resolvePartitionKey,
                                                                           params string[] containerPartitionKeyPath)
        {
            _ = pipelineBuilder ?? throw new ArgumentNullException(nameof(pipelineBuilder));
            _ = documentContainerFactory ?? throw new ArgumentNullException(nameof(documentContainerFactory));
            _ = leaseContainerFactory ?? throw new ArgumentNullException(nameof(leaseContainerFactory));
            _ = resolvePartitionKey ?? throw new ArgumentNullException(nameof(resolvePartitionKey));
            if (containerPartitionKeyPath is null || containerPartitionKeyPath.Length == 0)
            {
                throw new ArgumentException("A container partition-key path is required.", nameof(containerPartitionKeyPath));
            }

            // Clone before wrapping so post-registration mutation of the caller-owned array cannot corrupt the
            // registered path, and validate every segment: deferred (#219/#220) document writers stamp the resolved
            // partition-key value at this declared path, so an empty/whitespace segment would break the carriage contract.
            var partitionKeyPathSegments = (string[])containerPartitionKeyPath.Clone();
            for (var i = 0; i < partitionKeyPathSegments.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(partitionKeyPathSegments[i]))
                {
                    throw new ArgumentException("Every container partition-key path segment must be non-null and non-whitespace.", nameof(containerPartitionKeyPath));
                }
            }

            var partitionKeyPath = Array.AsReadOnly(partitionKeyPathSegments);
            var partitionKeyResolver = new PartitionKeyResolver(resolvePartitionKey, partitionKeyPath);

            pipelineBuilder.Services.Replace(ServiceLifetime.Singleton, sp => new DocumentContainer(documentContainerFactory(sp)));
            pipelineBuilder.Services.Replace(ServiceLifetime.Singleton, sp => new LeaseContainer(leaseContainerFactory(sp)));
            pipelineBuilder.Services.Replace<PartitionKeyResolver>(ServiceLifetime.Singleton, _ => partitionKeyResolver);
            pipelineBuilder.Services.Replace<DocumentTierReliabilitySurface, DocumentTierReliabilitySurface>(ServiceLifetime.Scoped);
            pipelineBuilder.Services.Replace<IDocumentTierReliabilitySurface>(ServiceLifetime.Scoped, sp => sp.GetRequiredService<DocumentTierReliabilitySurface>());

            // OUTERMOST: register first so the CommandBehaviorPipeline reverse places it outermost.
            pipelineBuilder.WithBehavior(typeof(DocumentTierBatchLifecycleBehavior<>));

            return pipelineBuilder;
        }
    }
}
