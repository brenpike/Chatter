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
            var staged = StageReliabilityRegistrations(pipelineBuilder.Services);

            StageUnitOfWorkBehavior<TContext>(staged);

            CommitStagedRegistrations(pipelineBuilder.Services, staged);

            return pipelineBuilder;
        }

        public static CommandPipelineBuilder WithInboxBehavior<TContext>(this CommandPipelineBuilder pipelineBuilder)
            where TContext : DbContext
        {
            var staged = StageReliabilityRegistrations(pipelineBuilder.Services);

            BindReliabilityContext<TContext>(staged);
            StageUnitOfWorkBehavior<TContext>(staged);
            SeedDefaultReliabilityRetention(staged);
            staged.Replace<BrokeredMessageInbox<TContext>, BrokeredMessageInbox<TContext>>(ServiceLifetime.Scoped);
            staged.Replace<IBrokeredMessageInbox>(ServiceLifetime.Scoped, sp => sp.GetRequiredService<BrokeredMessageInbox<TContext>>());
            AddReliabilityBehaviorOnce(staged, typeof(InboxBehavior<>));
            NormalizeReliabilityBehaviorOrder(staged);

            CommitStagedRegistrations(pipelineBuilder.Services, staged);

            return pipelineBuilder;
        }

        public static CommandPipelineBuilder WithOutboxProcessingBehavior<TContext>(this CommandPipelineBuilder pipelineBuilder)
            where TContext : DbContext
        {
            var staged = StageReliabilityRegistrations(pipelineBuilder.Services);

            BindReliabilityContext<TContext>(staged);
            AddReliabilityBehaviorOnce(staged, typeof(OutboxProcessingBehavior<>));
            SeedDefaultReliabilityRetention(staged);
            // Register the EF outbox once scoped and forward the enqueue contract to the same instance.
            // IPollableOutboxStore is obtained by casting IBrokeredMessageOutbox at the consumption site.
            staged.Replace<BrokeredMessageOutbox<TContext>, BrokeredMessageOutbox<TContext>>(ServiceLifetime.Scoped);
            staged.Replace<IBrokeredMessageOutbox>(ServiceLifetime.Scoped, sp => sp.GetRequiredService<BrokeredMessageOutbox<TContext>>());
            staged.Replace<IRouteBrokeredMessages, OutboxBrokeredMessageRouter>(ServiceLifetime.Scoped);
            StageUnitOfWorkBehavior<TContext>(staged);
            NormalizeReliabilityBehaviorOrder(staged);

            CommitStagedRegistrations(pipelineBuilder.Services, staged);

            return pipelineBuilder;
        }

        // The unit of work the inbox and outbox doors each bring with them. It takes the staged collection rather
        // than the builder so a door reaches it without leaving the staging, which is what keeps the commit the one
        // place any of them touches the caller's collection.
        private static void StageUnitOfWorkBehavior<TContext>(IServiceCollection services)
            where TContext : DbContext
        {
            BindReliabilityContext<TContext>(services);
            services.Replace<IUnitOfWork, UnitOfWork<TContext>>(ServiceLifetime.Scoped);
            AddReliabilityBehaviorOnce(services, typeof(UnitOfWorkBehavior<>));
            NormalizeReliabilityBehaviorOrder(services);
        }

        // INVARIANT: no entry point holds the caller's IServiceCollection while it does anything that can throw.
        // Every mutation a door makes is staged onto this copy, and the caller's collection is reached only through
        // CommitStagedRegistrations below, which is not fallible on its own account. A refusal - the mixed-context
        // one, any of the three span refusals, or one a step added later raises - therefore lands with the caller's
        // collection exactly as it was handed over, wherever in the door that step sits. This replaces an earlier
        // arrangement in which each door bound the context first and was correct only while that bind stayed the
        // first fallible statement: the span refusals were added to the retention door afterwards, BELOW a bind that
        // had already registered, and the convention was void with nothing saying so.
        // ELIMINATED CLASS: a reliability entry point mutating the caller's collection before, or despite, a refusal.
        // Pinned by UsingReliabilityPipelineExtensions/WhenBindingReliabilityContext
        // .MustLeaveTheServiceCollectionExactlyAsItWasWhenAnyRefusalIsRaised, a sweep over every public
        // TContext-parameterized entry point crossed with every refusal that entry point can raise. Returning
        // `services` from here instead of the copy reddens that sweep's six span cases and nothing else in this
        // package (observed), because the commit then finds every slot already holding the descriptor it would write
        // and writes nothing - which is precisely the pre-staging behaviour.
        private static IServiceCollection StageReliabilityRegistrations(IServiceCollection services)
        {
            // ServiceCollection implements IList<ServiceDescriptor> explicitly, so the staging is held through the
            // interface rather than through the concrete type, which exposes no Add of its own.
            IServiceCollection staged = new ServiceCollection();

            for (var index = 0; index < services.Count; index++)
            {
                staged.Add(services[index]);
            }

            return staged;
        }

        // NOTE: descriptors already sitting in the slot the staging would write are left untouched rather than
        // removed and re-added, because a host may hand these extensions a decorating or side-effecting
        // IServiceCollection that would see every replay. A slot is written only where it does not already hold the
        // staged descriptor, a longer staging appends its tail, and a shorter one has its surplus removed from the
        // back so a removal never invalidates an index still to be visited. Writing by absolute index is also what
        // carries NormalizeReliabilityBehaviorOrder's slot permutation across intact. A collection the host has made
        // read-only is expected to refuse the write here just as it refused the registration before staging existed;
        // no test pins that, and it is written down so a reader does not assume one does. Why the staging is there at
        // all is stated once, on StageReliabilityRegistrations above.
        private static void CommitStagedRegistrations(IServiceCollection services, IServiceCollection staged)
        {
            for (var index = services.Count - 1; index >= staged.Count; index--)
            {
                services.RemoveAt(index);
            }

            for (var index = 0; index < staged.Count; index++)
            {
                if (index >= services.Count)
                {
                    services.Add(staged[index]);
                }
                else if (!ReferenceEquals(services[index], staged[index]))
                {
                    services[index] = staged[index];
                }
            }
        }

        /// <summary>
        /// Configures how long the relational inbox and outbox keep their rows, and starts the purge that enforces it.
        /// </summary>
        /// <param name="retentionOptions">A delegate configuring the <see cref="EntityFrameworkReliabilityOptions"/></param>
        /// <exception cref="ArgumentOutOfRangeException">A configured span fell outside the range the operation reading it can compute with</exception>
        /// <exception cref="InvalidOperationException">A different <typeparamref name="TContext"/> is already bound to these reliability extensions</exception>
        /// <remarks>
        /// INVARIANT: ONE door spans both tables. Retention is a property of the stored rows rather than of either
        /// behavior, so a per-behavior overload would make the result depend on which behaviors a host happened to
        /// register; this door reads the same whatever order it is called in. The last call wins - the registration is
        /// replaced rather than appended - while the purge service is added once however many times this is called.
        /// Every span is refused HERE, at registration, rather than when the code that reads it runs, so the host
        /// must not start before a span outside its consumer's range is rejected. Which range, and why each bound
        /// is where it is, is stated once on RefuseSpanOutsideUsableRange below.
        /// </remarks>
        public static CommandPipelineBuilder WithReliabilityRetention<TContext>(this CommandPipelineBuilder pipelineBuilder,
                                                                                Action<EntityFrameworkReliabilityOptions> retentionOptions)
            where TContext : DbContext
        {
            var staged = StageReliabilityRegistrations(pipelineBuilder.Services);

            BindReliabilityContext<TContext>(staged);

            var options = new EntityFrameworkReliabilityOptions();
            retentionOptions?.Invoke(options);

            // The distance back to DateTime.MinValue, read ONCE here and used as the ceiling for both retention
            // windows. Reading it at registration is what makes the refusal permanent rather than a snapshot:
            // UtcNow only advances, so a window subtractable from the clock now is subtractable from every later
            // clock, and a window this door admits can never become unsubtractable while the host runs.
            var largestSubtractableWindow = DateTime.UtcNow - DateTime.MinValue;

            RefuseSpanOutsideUsableRange(options.InboxDeduplicationWindow,
                                         nameof(EntityFrameworkReliabilityOptions.InboxDeduplicationWindow),
                                         largestSubtractableWindow,
                                         "no cutoff can be derived from a window that long");
            RefuseSpanOutsideUsableRange(options.ProcessedOutboxRetention,
                                         nameof(EntityFrameworkReliabilityOptions.ProcessedOutboxRetention),
                                         largestSubtractableWindow,
                                         "no cutoff can be derived from a window that long");
            RefuseSpanOutsideUsableRange(options.PurgeInterval,
                                         nameof(EntityFrameworkReliabilityOptions.PurgeInterval),
                                         MaxSchedulablePurgeInterval,
                                         "the scheduler cannot wait that long");

            RemoveReliabilityRetention(staged);
            staged.AddSingleton(options);
            staged.AddHostedService<ReliabilityRetentionPurgeService<TContext>>();

            CommitStagedRegistrations(pipelineBuilder.Services, staged);

            return pipelineBuilder;
        }

        // INVARIANT: ONE DbContext spans every reliability extension on a pipeline, and this is what holds each
        // TContext-parameterized entry point to it. The unit of work commits the context the inbox marker, the outbox
        // rows and the retention purge all live in; because each entry point takes its own type argument and Replace
        // is remove-then-add, a second context would otherwise leave the unit of work committing one context while the
        // inbox wrote its marker into another - the once-only guarantee lost with nothing raised. The same context is
        // a no-op however many times, and however many entry points, it arrives through. This runs against the staged
        // copy like every other step, so what a refused call leaves behind is StageReliabilityRegistrations'
        // guarantee rather than this method's position within a door; that reasoning lives there and is not restated
        // here. Pinned by UsingReliabilityPipelineExtensions/WhenBindingReliabilityContext
        // .MustRefuseASecondContextFromEveryContextParameterizedEntryPoint, which drives every discovered entry point
        // with a second context; deleting this call from any one door reddens that sweep for that door alone.
        private static void BindReliabilityContext<TContext>(IServiceCollection services)
            where TContext : DbContext
        {
            var boundContextType = FindBoundReliabilityContextType(services);

            if (boundContextType is null)
            {
                services.AddSingleton(new ReliabilityContextBinding(typeof(TContext)));
                return;
            }

            if (boundContextType == typeof(TContext))
            {
                return;
            }

            throw new InvalidOperationException(
                $"The reliability extensions are already bound to DbContext '{boundContextType.FullName}' and cannot also "
                + $"be bound to '{typeof(TContext).FullName}'. The unit of work commits the context that holds the inbox "
                + "marker, the outbox rows and the rows retention purges, so every reliability extension on one command "
                + "pipeline must be given the same DbContext.");
        }

        // INVARIANT: the IsKeyedService test must stay ahead of the ImplementationInstance read, for the same reason it
        // stays ahead of the ImplementationType read in IsBehaviorDescriptorFor below. On an affected Microsoft
        // .Extensions.DependencyInjection.Abstractions, reading ImplementationInstance for a keyed descriptor throws
        // InvalidOperationException, and consumers bind that assembly at their ASP.NET Core host's patch level rather
        // than at the version restored here, so reordering these operands reintroduces the crash on an unpatched host
        // without failing anything in this repository. WHICH versions are affected, and why the boundary must be read
        // from the LOADED assembly rather than from the lock file, is recorded ONCE - above
        // UsingReliabilityPipelineExtensions/WhenOrderingReliabilityBehaviors
        // .MustLeaveAKeyedCommandBehaviorOutOfTheReliabilityBehaviorSet. Do not restate the boundary here: a version
        // literal repeated per site is exactly what drifted.
        private static Type FindBoundReliabilityContextType(IServiceCollection services)
        {
            for (var index = 0; index < services.Count; index++)
            {
                var descriptor = services[index];

                if (!descriptor.IsKeyedService
                    && descriptor.ServiceType == typeof(ReliabilityContextBinding)
                    && descriptor.ImplementationInstance is ReliabilityContextBinding binding)
                {
                    return binding.ContextType;
                }
            }

            return null;
        }

        // The largest interval Task.Delay accepts. Measured rather than quoted: Task.Delay takes
        // TimeSpan.FromMilliseconds(uint.MaxValue - 1) and throws ArgumentOutOfRangeException(paramName: "delay")
        // one millisecond above it, identically on net8.0 and net10.0 (observed), so one constant serves both
        // targets and no TFM conditional is needed.
        private static readonly TimeSpan MaxSchedulablePurgeInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

        // INVARIANT: this door admits a span only from the range the ONE operation that consumes it can actually
        // compute with - Task.Delay for the interval, subtraction from DateTime.UtcNow for either window - so a
        // span that would fault its consumer cannot reach it. ELIMINATED CLASS: a configured span accepted here
        // and refused later by the code that reads it. Both bounds matter and neither is a policy on duration.
        // Below the floor, a window naming no time deletes every row it can reach and an interval naming no time
        // spins the purge loop against the database with no wait. Above the ceiling, the failure is louder and
        // arrives after the host is already up: Task.Delay throws on the FIRST wait and ExecuteAsync catches only
        // cancellation, so the hosted service ends and retention silently stops, while an unsubtractable window
        // throws on every receive in BrokeredMessageInbox and on every purge pass instead of deduplicating or
        // reclaiming anything. What is deliberately NOT bounded is how long an operator keeps rows or waits
        // between passes: the ceiling is the consumer's own domain, which is why a thousand-year window still
        // registers. Pinned by UsingReliabilityPipelineExtensions/WhenConfiguringReliabilityBehaviors, five facts
        // holding the range from both sides. Raising MaxSchedulablePurgeInterval by one millisecond reddens
        // MustRefuseAPurgeIntervalTheSchedulerCannotSchedule alone; lowering it by one tick reddens
        // MustAcceptTheLargestPurgeIntervalTheSchedulerCanSchedule alone; passing TimeSpan.MaxValue as either
        // window's ceiling reddens MustRefuseAnInboxDeduplicationWindowNoCutoffCanBeDerivedFrom or
        // MustRefuseAProcessedOutboxRetentionNoCutoffCanBeDerivedFrom, one per call site; and narrowing either
        // window's ceiling to an invented duration reddens MustAcceptARetentionWindowACutoffCanStillBeDerivedFrom
        // alone (observed).
        private static void RefuseSpanOutsideUsableRange(TimeSpan? span, string optionName, TimeSpan ceiling, string ceilingReason)
        {
            if (!span.HasValue)
            {
                return;
            }

            if (span.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(optionName,
                                                      span.Value,
                                                      $"{nameof(EntityFrameworkReliabilityOptions)}.{optionName} must be a positive time span.");
            }

            if (span.Value > ceiling)
            {
                throw new ArgumentOutOfRangeException(optionName,
                                                      span.Value,
                                                      $"{nameof(EntityFrameworkReliabilityOptions)}.{optionName} must be no longer than "
                                                      + $"'{ceiling}' because {ceilingReason}.");
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
        private static void AddReliabilityBehaviorOnce(IServiceCollection services, Type openGenericBehaviorType)
        {
            if (services.Any(descriptor => IsBehaviorDescriptorFor(descriptor, openGenericBehaviorType)))
            {
                return;
            }

            services.AddPipelineBehavior(openGenericBehaviorType);
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
        // INVARIANT: the IsKeyedService test must stay ahead of the ImplementationType read. On an affected
        // Microsoft.Extensions.DependencyInjection.Abstractions, reading ImplementationType for a keyed descriptor
        // throws InvalidOperationException, and consumers bind that assembly at their ASP.NET Core host's patch
        // level, so reordering these operands reintroduces the crash on an unpatched host without failing anything
        // here. The affected-version boundary is recorded ONCE, above
        // UsingReliabilityPipelineExtensions/WhenOrderingReliabilityBehaviors
        // .MustLeaveAKeyedCommandBehaviorOutOfTheReliabilityBehaviorSet; do not restate it here.
        private static bool IsBehaviorDescriptorFor(ServiceDescriptor descriptor, Type openGenericBehaviorType)
            => !descriptor.IsKeyedService
                && descriptor.ServiceType.IsGenericType
                && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(ICommandBehavior<>)
                && descriptor.ImplementationType == openGenericBehaviorType;
    }
}
