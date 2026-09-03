using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;

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

            StandaloneRelayProcessorRegistry processorRegistry = GetOrAddProcessorRegistry(services);

            // Registration-time fail-fast for DECLARED identities. ValidateOptions has already enforced that the two source
            // identities are supplied as a non-whitespace PAIR or both null, so a non-null MonitoredSourceIdentity implies a
            // non-null LeaseSourceIdentity here. A declared identity keys the processor name on the caller-declared tokens (not
            // the resolved handles), so the name is derivable NOW — a duplicate declared pair is rejected synchronously rather
            // than silently forming one consumer group that wedges a filtered-out document at runtime. Ground-truth-defaulted
            // relays (both identities null) are NOT checked here — their name is resolvable only after the containers are
            // resolved, so the StandaloneCosmosOutboxRelayHostedService start-time backstop guards them instead.
            if (options.MonitoredSourceIdentity is not null && options.LeaseSourceIdentity is not null)
            {
                string declaredProcessorName = CosmosOutboxRelayHostedService.BuildProcessorName(
                    CosmosOutboxRelayHostedService.RelaySourceIdentityKey.ForDeclared(options.MonitoredSourceIdentity, options.LeaseSourceIdentity));
                processorRegistry.RegisterDeclaredProcessorOrThrow(declaredProcessorName, options.MonitoredSourceIdentity, options.LeaseSourceIdentity);
            }

            // Register a distinct singleton IHostedService per call (factory-built, so the registration never dedupes): two
            // calls yield two relays. The host resolves IMessagingInfrastructureProvider + IBodyConverterFactory from DI and
            // captures the validated options. The logger is resolved with GetService, NOT GetRequiredService: an
            // application that configured no logging must still get a working relay (the host treats a null logger as a
            // silent no-op), but one that DID configure logging must get the always-on give-up / change-feed-fault log
            // channel — a factory-built host is injected nothing it does not ask for.
            services.AddSingleton<IHostedService>(serviceProvider => new StandaloneCosmosOutboxRelayHostedService(
                serviceProvider,
                serviceProvider.GetRequiredService<IMessagingInfrastructureProvider>(),
                serviceProvider.GetRequiredService<IBodyConverterFactory>(),
                options,
                processorRegistry,
                serviceProvider.GetService<ILogger<StandaloneCosmosOutboxRelayHostedService>>()));

            return services;
        }

        /// <summary>
        /// SAFE-BY-DEFAULT overload: registers a standalone Cosmos outbox relay AND wires its
        /// <see cref="CosmosOutboxRelayOptions.BodyResolverFactory"/> to a typed <see cref="IOutboxBodyResolver"/>
        /// implementation, so the common case needs no knowledge of the raw factory escape hatch. <typeparamref name="TResolver"/>
        /// is registered <c>Scoped</c> (via <c>TryAdd</c>, so an existing registration is preserved) and resolved from the
        /// host-owned per-document <see cref="IServiceScope"/> — a fresh instance per drained document — so it may depend on
        /// scoped services. Use the raw <see cref="AddCosmosOutboxRelay(IServiceCollection, Action{CosmosOutboxRelayOptions})"/>
        /// overload only when you need full control over how the resolver is obtained.
        /// </summary>
        /// <remarks>This overload OWNS the <see cref="CosmosOutboxRelayOptions.BodyResolverFactory"/> wiring: if
        /// <paramref name="configure"/> ALSO sets it, an <see cref="ArgumentException"/> is thrown rather than silently
        /// overriding the caller's intent.</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="configure"/> also sets
        /// <see cref="CosmosOutboxRelayOptions.BodyResolverFactory"/>, or the configured options omit a required factory or
        /// partition-key path.</exception>
        public static IServiceCollection AddCosmosOutboxRelay<TResolver>(this IServiceCollection services, Action<CosmosOutboxRelayOptions> configure)
            where TResolver : class, IOutboxBodyResolver
        {
            _ = services ?? throw new ArgumentNullException(nameof(services));
            _ = configure ?? throw new ArgumentNullException(nameof(configure));

            TryAddScopedResolver(services, ServiceDescriptor.Scoped(typeof(TResolver), typeof(TResolver)));

            return AddCosmosOutboxRelay(services, options =>
            {
                configure(options);
                ThrowIfCallerWiredBodyResolverFactory(options);
                options.BodyResolverFactory = serviceProvider => serviceProvider.GetRequiredService<TResolver>();
            });
        }

        /// <summary>
        /// KEYED overload for applications that run MULTIPLE standalone relays (one per monitored container) each needing its
        /// OWN <see cref="IOutboxBodyResolver"/>: registers <typeparamref name="TResolver"/> as a keyed-scoped
        /// <see cref="IOutboxBodyResolver"/> under <paramref name="serviceKey"/> (via <c>TryAdd</c>) and wires this relay's
        /// <see cref="CosmosOutboxRelayOptions.BodyResolverFactory"/> to resolve THAT keyed resolver from the host-owned
        /// per-document scope. Each relay binds its own keyed resolver, so two relays with two distinct keys never collide.
        /// </summary>
        /// <remarks>This overload OWNS the <see cref="CosmosOutboxRelayOptions.BodyResolverFactory"/> wiring: if
        /// <paramref name="configure"/> ALSO sets it, an <see cref="ArgumentException"/> is thrown rather than silently
        /// overriding the caller's intent.</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="serviceKey"/>, or
        /// <paramref name="configure"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="configure"/> also sets
        /// <see cref="CosmosOutboxRelayOptions.BodyResolverFactory"/>, or the configured options omit a required factory or
        /// partition-key path.</exception>
        public static IServiceCollection AddCosmosOutboxRelay<TResolver>(this IServiceCollection services, object serviceKey, Action<CosmosOutboxRelayOptions> configure)
            where TResolver : class, IOutboxBodyResolver
        {
            _ = services ?? throw new ArgumentNullException(nameof(services));
            _ = serviceKey ?? throw new ArgumentNullException(nameof(serviceKey));
            _ = configure ?? throw new ArgumentNullException(nameof(configure));

            // Register keyed AS IOutboxBodyResolver so the wired factory resolves GetRequiredKeyedService<IOutboxBodyResolver>(key) —
            // this lets multiple monitored containers each bind their own keyed resolver under a distinct key.
            TryAddScopedResolver(services, ServiceDescriptor.KeyedScoped(typeof(IOutboxBodyResolver), serviceKey, typeof(TResolver)));

            return AddCosmosOutboxRelay(services, options =>
            {
                configure(options);
                ThrowIfCallerWiredBodyResolverFactory(options);
                options.BodyResolverFactory = serviceProvider => serviceProvider.GetRequiredKeyedService<IOutboxBodyResolver>(serviceKey);
            });
        }

        // Mirrors Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAdd: the
        // resolver descriptor is added only when no existing registration matches the SAME (service type, service key) pair,
        // so a caller's prior registration is preserved rather than duplicated/replaced. Reimplemented here because this
        // module declares its own static `Extensions` type in the Microsoft.Extensions.DependencyInjection namespace, which
        // shadows the Microsoft.Extensions.DependencyInjection.Extensions namespace and makes the framework TryAdd extension
        // methods unreachable from this file by name.
        private static void TryAddScopedResolver(IServiceCollection services, ServiceDescriptor descriptor)
        {
            foreach (ServiceDescriptor existing in services)
            {
                if (existing.ServiceType == descriptor.ServiceType
                    && existing.IsKeyedService == descriptor.IsKeyedService
                    && (!descriptor.IsKeyedService || Equals(existing.ServiceKey, descriptor.ServiceKey)))
                {
                    return;
                }
            }

            services.Add(descriptor);
        }

        // The typed/keyed overloads OWN the BodyResolverFactory wiring. If the caller's configure also set it, fail loudly
        // (do not silently override the caller's delegate): either the typed/keyed overload wires the resolver, or the raw
        // AddCosmosOutboxRelay(configure) escape hatch is used and the caller sets BodyResolverFactory itself — never both.
        private static void ThrowIfCallerWiredBodyResolverFactory(CosmosOutboxRelayOptions options)
        {
            if (options.BodyResolverFactory is not null)
            {
                throw new ArgumentException(
                    "AddCosmosOutboxRelay<TResolver> owns the CosmosOutboxRelayOptions.BodyResolverFactory wiring (it binds the registered resolver). Do not also set BodyResolverFactory in configure — either use the typed/keyed overload and let it wire the resolver, or use the raw AddCosmosOutboxRelay(configure) escape hatch and set BodyResolverFactory yourself.",
                    "configure");
            }
        }

        // Resolves the single shared standalone-relay processor-name registry singleton instance (so every AddCosmosOutboxRelay
        // call on this collection accumulates into ONE registry), creating and registering it on first call. Mirrors
        // Extensions.GetOrCreateRegistry's marker-singleton (ImplementationInstance) pattern.
        private static StandaloneRelayProcessorRegistry GetOrAddProcessorRegistry(IServiceCollection services)
        {
            ServiceDescriptor existing = services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(StandaloneRelayProcessorRegistry));
            if (existing?.ImplementationInstance is StandaloneRelayProcessorRegistry registry)
            {
                return registry;
            }

            registry = new StandaloneRelayProcessorRegistry();
            services.AddSingleton(registry);
            return registry;
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

            // Validate the partition-key path (non-empty, every segment non-whitespace) and store an INDEPENDENT read-only
            // snapshot back onto the options through the SAME hardened validator the command-pipeline registration uses
            // (PartitionKeyPathValidator). The relay recovers each drained document's partition key at these declared
            // segments to target the delivered/TTL patch, so an empty/whitespace/null segment would make recovery read the
            // whole document (or null) instead of the actual path — the delivered patch would then target the wrong logical
            // partition and the document would stay pending/replay. Storing the frozen snapshot back makes the freeze
            // effective for the captured options the host later reads (post-registration mutation of the caller-owned
            // collection cannot corrupt the registered path).
            options.PartitionKeyPath = PartitionKeyPathValidator.ValidateAndSnapshot(options.PartitionKeyPath, nameof(options.PartitionKeyPath));

            // Source identities must be declared as a PAIR or omitted as a pair. StandaloneCosmosOutboxRelayHostedService
            // .BuildSourceIdentityKey routes to the DECLARED key when EITHER side is non-null, turning a missing/blank side
            // into an empty string — so two relays over different monitored containers that declare only the SAME lease
            // identity would derive the same processor name/lease identity and contend for the same lease documents. Require
            // both non-whitespace (declared) or both null (ground-truth keying on the resolved containers' identity),
            // mirroring the command-pipeline registration which requires both source identities.
            if (options.MonitoredSourceIdentity is not null || options.LeaseSourceIdentity is not null)
            {
                if (string.IsNullOrWhiteSpace(options.MonitoredSourceIdentity) || string.IsNullOrWhiteSpace(options.LeaseSourceIdentity))
                {
                    throw new ArgumentException(
                        "MonitoredSourceIdentity and LeaseSourceIdentity must be supplied together as non-whitespace values, or both left null to key on the resolved containers' ground-truth identity. Declaring or blanking only one side derives a processor/lease identity from an empty token, which can collide with another standalone relay's lease.",
                        nameof(options.MonitoredSourceIdentity));
                }
            }

            // Eagerly map the options into the relay's delivery settings so the F2 construction invariants (delivered status
            // non-empty and != pending, delivered TTL > 0 including the -1 "retain indefinitely" rejection, status patch path
            // anchored to the status field, ttl patch path a valid JSON pointer, — for an enabled poison policy — a
            // non-negative threshold plus a poison status that is non-empty and differs from both pending and the delivered
            // status, and — ALWAYS, because the post-publish brake has no off switch — a POSITIVE published-unconfirmed
            // threshold plus an unconfirmed status that is non-empty and differs from pending, the delivered status and the
            // poison status) throw a clear ArgumentException AT
            // REGISTRATION — before the service provider is built — instead of deferring the failure to host construction.
            // This reuses the SAME validating builder the host uses, so the checks are not duplicated here.
            _ = OutboxDeliverySettings.FromOptions(options);
        }
    }
}
