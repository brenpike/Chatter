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
            // Register the EF outbox once scoped and forward the enqueue contract to the same instance.
            // IPollableOutboxStore is obtained by casting IBrokeredMessageOutbox at the consumption site.
            pipelineBuilder.Services.Replace<BrokeredMessageOutbox<TContext>, BrokeredMessageOutbox<TContext>>(ServiceLifetime.Scoped);
            pipelineBuilder.Services.Replace<IBrokeredMessageOutbox>(ServiceLifetime.Scoped, sp => sp.GetRequiredService<BrokeredMessageOutbox<TContext>>());
            pipelineBuilder.Services.Replace<IRouteBrokeredMessages, OutboxBrokeredMessageRouter>(ServiceLifetime.Scoped);
            pipelineBuilder.WithUnitOfWorkBehavior<TContext>();
            NormalizeReliabilityBehaviorOrder(pipelineBuilder.Services);

            return pipelineBuilder;
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
        // registration - which serves a single command type - out of the reliability behavior set.
        private static bool IsBehaviorDescriptorFor(ServiceDescriptor descriptor, Type openGenericBehaviorType)
            => descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(ICommandBehavior<>)
                && descriptor.ImplementationType == openGenericBehaviorType;
    }
}
