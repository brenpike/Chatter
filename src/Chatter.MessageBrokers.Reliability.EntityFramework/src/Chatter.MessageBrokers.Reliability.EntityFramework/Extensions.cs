using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.EntityFramework;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        // INVARIANT: the reliability behaviors resolve outermost-first, because CommandBehaviorPipeline
        // composes them last-to-first. Outbox processing awaits the handler and only then dispatches the
        // outbox rows to the broker, and the transactional outbox requires those rows to be committed before
        // they are dispatched, so outbox processing sits OUTSIDE the unit of work. The unit of work opens the
        // transaction and runs the rest of the pipeline inside it. The inbox marker must commit atomically
        // with the handler's own work - that is the once-only guarantee - so the inbox sits INSIDE the unit
        // of work. Hence: outbox processing wraps the unit of work, which wraps the inbox.
        private static readonly Type[] _reliabilityBehaviorOrder = new[]
        {
            typeof(OutboxProcessingBehavior<>),
            typeof(UnitOfWorkBehavior<>),
            typeof(InboxBehavior<>)
        };

        public static CommandPipelineBuilder WithUnitOfWorkBehavior<TContext>(this CommandPipelineBuilder pipelineBuilder)
            where TContext : DbContext
        {
            pipelineBuilder.Services.Replace<IUnitOfWork, UnitOfWork<TContext>>(ServiceLifetime.Scoped);
            AddReliabilityBehaviorOnce(pipelineBuilder, typeof(UnitOfWorkBehavior<>));
            NormalizeReliabilityBehaviorOrder(pipelineBuilder.Services);

            return pipelineBuilder;
        }

        public static CommandPipelineBuilder WithInboxBehavior<TContext>(this CommandPipelineBuilder pipelineBuilder)
            where TContext : DbContext
        {
            pipelineBuilder.WithUnitOfWorkBehavior<TContext>();
            SeedDefaultReliabilityRetention(pipelineBuilder.Services);
            pipelineBuilder.Services.Replace<BrokeredMessageInbox<TContext>, BrokeredMessageInbox<TContext>>(ServiceLifetime.Scoped);
            pipelineBuilder.Services.Replace<IBrokeredMessageInbox>(ServiceLifetime.Scoped, sp => sp.GetRequiredService<BrokeredMessageInbox<TContext>>());
            AddReliabilityBehaviorOnce(pipelineBuilder, typeof(InboxBehavior<>));
            NormalizeReliabilityBehaviorOrder(pipelineBuilder.Services);

            return pipelineBuilder;
        }

        public static CommandPipelineBuilder WithOutboxProcessingBehavior<TContext>(this CommandPipelineBuilder pipelineBuilder)
            where TContext : DbContext
        {
            AddReliabilityBehaviorOnce(pipelineBuilder, typeof(OutboxProcessingBehavior<>));
            SeedDefaultReliabilityRetention(pipelineBuilder.Services);
            // Register the EF outbox once scoped and forward the enqueue contract to the same instance.
            // IPollableOutboxStore is obtained by casting IBrokeredMessageOutbox at the consumption site.
            pipelineBuilder.Services.Replace<BrokeredMessageOutbox<TContext>, BrokeredMessageOutbox<TContext>>(ServiceLifetime.Scoped);
            pipelineBuilder.Services.Replace<IBrokeredMessageOutbox>(ServiceLifetime.Scoped, sp => sp.GetRequiredService<BrokeredMessageOutbox<TContext>>());
            pipelineBuilder.Services.Replace<IRouteBrokeredMessages, OutboxBrokeredMessageRouter>(ServiceLifetime.Scoped);
            pipelineBuilder.WithUnitOfWorkBehavior<TContext>();
            NormalizeReliabilityBehaviorOrder(pipelineBuilder.Services);

            return pipelineBuilder;
        }

        /// <summary>
        /// Configures how long the relational inbox and outbox keep their rows, and starts the purge that enforces it.
        /// </summary>
        /// <param name="retentionOptions">A delegate configuring the <see cref="EntityFrameworkReliabilityOptions"/></param>
        /// <exception cref="ArgumentOutOfRangeException">A configured span was not positive</exception>
        /// <remarks>
        /// INVARIANT: ONE door spans both tables. Retention is a property of the stored rows rather than of either
        /// behavior, so a per-behavior overload would make the result depend on which behaviors a host happened to
        /// register; this door reads the same whatever order it is called in. The last call wins - the registration is
        /// replaced rather than appended - while the purge service is added once however many times this is called.
        /// Every span is refused HERE, at registration, rather than when the first purge runs: a window that names no
        /// time would delete every row it can reach, and the host must not start before that is rejected.
        /// </remarks>
        public static CommandPipelineBuilder WithReliabilityRetention<TContext>(this CommandPipelineBuilder pipelineBuilder,
                                                                                Action<EntityFrameworkReliabilityOptions> retentionOptions)
            where TContext : DbContext
        {
            var options = new EntityFrameworkReliabilityOptions();
            retentionOptions?.Invoke(options);

            RefuseNonPositiveSpan(options.InboxDeduplicationWindow, nameof(EntityFrameworkReliabilityOptions.InboxDeduplicationWindow));
            RefuseNonPositiveSpan(options.ProcessedOutboxRetention, nameof(EntityFrameworkReliabilityOptions.ProcessedOutboxRetention));
            RefuseNonPositiveSpan(options.PurgeInterval, nameof(EntityFrameworkReliabilityOptions.PurgeInterval));

            RemoveReliabilityRetention(pipelineBuilder.Services);
            pipelineBuilder.Services.AddSingleton(options);
            pipelineBuilder.Services.AddHostedService<ReliabilityRetentionPurgeService<TContext>>();

            return pipelineBuilder;
        }

        private static void RefuseNonPositiveSpan(TimeSpan? span, string optionName)
        {
            if (span.HasValue && span.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(optionName,
                                                      span.Value,
                                                      $"{nameof(EntityFrameworkReliabilityOptions)}.{optionName} must be a positive time span.");
            }
        }

        // INVARIANT: RemoveAll<T> and TryAddSingleton<T> live in the Microsoft.Extensions.DependencyInjection
        // .Extensions namespace, and THIS type shadows that namespace from inside Microsoft.Extensions
        // .DependencyInjection - importing it raises CS0138 ('Extensions' is a type not a namespace) plus CS0437 - so
        // neither is reachable by name from this file and both are written out against IServiceCollection's own
        // surface. The same collision is recorded in Chatter.MessageBrokers.Reliability.Cosmos
        // (CosmosOutboxRelayServiceCollectionExtensions.TryAddScopedResolver). The removal walks back to front so a
        // removal never invalidates an index still to be visited.
        private static void RemoveReliabilityRetention(IServiceCollection services)
        {
            for (var index = services.Count - 1; index >= 0; index--)
            {
                if (services[index].ServiceType == typeof(EntityFrameworkReliabilityOptions))
                {
                    services.RemoveAt(index);
                }
            }
        }

        // Seeds the disabled-by-default retention options so the inbox resolves an instance whatever order the
        // reliability extensions were called in. An instance a WithReliabilityRetention call already registered is
        // left alone: the default must never overwrite what an operator configured.
        private static void SeedDefaultReliabilityRetention(IServiceCollection services)
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(EntityFrameworkReliabilityOptions)))
            {
                return;
            }

            services.AddSingleton(new EntityFrameworkReliabilityOptions());
        }

        // INVARIANT: each reliability behavior is registered by its first caller only. Registering one again
        // removes its existing descriptor and appends the replacement, and that churn drags every descriptor
        // registered since - including descriptors this package does not own - out of the slot its own
        // registration chose. The relative order of the reliability behaviors themselves is established by
        // NormalizeReliabilityBehaviorOrder, not by this guard.
        private static void AddReliabilityBehaviorOnce(CommandPipelineBuilder pipelineBuilder, Type openGenericBehaviorType)
        {
            if (pipelineBuilder.Services.Any(descriptor => IsBehaviorDescriptorFor(descriptor, openGenericBehaviorType)))
            {
                return;
            }

            pipelineBuilder.WithBehavior(openGenericBehaviorType);
        }

        // INVARIANT: the reliability behaviors are permuted into the very indices they already occupy, so
        // every other descriptor keeps its absolute position and the behaviors are reordered only relative to
        // one another. Nothing is removed, inserted or appended.
        private static void NormalizeReliabilityBehaviorOrder(IServiceCollection services)
        {
            var slots = new List<int>();
            var behaviors = new List<ServiceDescriptor>();

            for (var index = 0; index < services.Count; index++)
            {
                if (IsReliabilityBehaviorDescriptor(services[index]))
                {
                    slots.Add(index);
                    behaviors.Add(services[index]);
                }
            }

            if (behaviors.Count < 2)
            {
                return;
            }

            var ordered = behaviors
                .OrderBy(descriptor => Array.IndexOf(_reliabilityBehaviorOrder, descriptor.ImplementationType))
                .ToList();

            for (var slot = 0; slot < slots.Count; slot++)
            {
                services[slots[slot]] = ordered[slot];
            }
        }

        private static bool IsReliabilityBehaviorDescriptor(ServiceDescriptor descriptor)
            => _reliabilityBehaviorOrder.Any(behaviorType => IsBehaviorDescriptorFor(descriptor, behaviorType));

        // The command behavior service type is what keeps this away from the IUnitOfWork -> UnitOfWork<TContext>
        // descriptor, and the exact open generic match is what leaves an application's own closed-generic
        // registration - which serves a single command type - out of the reliability behavior set. A keyed
        // descriptor is left out too: it is invisible to the non-keyed resolution the pipeline performs, so it
        // is never part of the sequence these extensions order.
        // INVARIANT: the IsKeyedService test must stay ahead of the ImplementationType read. Microsoft.Extensions
        // .DependencyInjection.Abstractions 8.0.0 throws InvalidOperationException from ImplementationType for a
        // keyed descriptor, and consumers bind that assembly at their ASP.NET Core host's patch level, so
        // reordering these operands reintroduces the crash on an unpatched host without failing anything here.
        private static bool IsBehaviorDescriptorFor(ServiceDescriptor descriptor, Type openGenericBehaviorType)
            => !descriptor.IsKeyedService
                && descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(ICommandBehavior<>)
                && descriptor.ImplementationType == openGenericBehaviorType;
    }
}
