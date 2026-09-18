using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.RabbitMQ.Receiving.CircuitBreaker;
using Chatter.MessageBrokers.RabbitMQ.Receiving.Retry;
using Chatter.MessageBrokers.RabbitMQ.Sending;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using System;
using System.Linq;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        public static RabbitMqOptionsBuilder AddRabbitMqOptions(this IServiceCollection services)
            => new RabbitMqOptionsBuilder(services);

        public static IChatterBuilder AddRabbitMq(this IChatterBuilder builder, Action<RabbitMqOptionsBuilder> optionsBuilder = null)
        {
            var optBuilder = builder.Services.AddRabbitMqOptions();
            optionsBuilder?.Invoke(optBuilder);
            var options = optBuilder.Build();

            // FullAtomicityViaInfrastructure is unsupported on RabbitMQ (no atomic receive-and-send across the
            // consume and a downstream publish); reject it at registration rather than letting a configured host
            // silently degrade at first send. None/ReceiveOnly are supported.
            RejectFullAtomicity(builder.Services);

            // The singleton IRabbitMqConnectionSource owns ONE receive channel and ONE consume-registration delegate,
            // but core creates one BrokeredMessageReceiver per discovered receiver. Two-or-more RabbitMQ queue
            // receivers would have the second StartReceivingAsync clobber the first, and on recovery only the last
            // would re-register — silently stalling the others. Multi-receiver support is DEFERRED; until then this
            // fails fast at registration. RejectFullAtomicity runs first to preserve the existing FullAtomicity-guard
            // test expectations.
            RejectMultipleReceivers(builder.Services);

            // AddIfNotRegistered below PRESERVES a consumer's own IRabbitMqConnectionSource descriptor at WHATEVER
            // lifetime it was registered with, but the IMessagingInfrastructure factory resolves the receiver's
            // source from the ROOT provider. A non-singleton override is therefore unusable — reject it here rather
            // than at first resolution. Runs before the registration below so the guard reads the consumer's own
            // descriptor, and after the other two guards to preserve their existing precedence.
            RejectNonSingletonConnectionSourceOverride(builder.Services);

            // INVARIANT: one IConnection per process — IRabbitMqConnectionSource is a SINGLETON. This is the one
            // deliberate lifetime divergence from the SqlServiceBroker fold (whose ISqlConnectionSource is Scoped):
            // the AMQP connection, its serialized receive channel, and its pooled publish channels are process-wide
            // and must not be re-created per scope.
            builder.Services.AddIfNotRegistered<IRabbitMqConnectionSource, RabbitMqConnectionSource>(ServiceLifetime.Singleton);

            builder.Services.AddIfNotRegistered<RabbitMqSender>(ServiceLifetime.Scoped);

            builder.Services.AddSingleton<ICircuitBreakerExceptionPredicatesProvider, RabbitMqCircuitBreakerExceptionPredicatesProvider>();
            builder.Services.AddSingleton<IRetryExceptionPredicatesProvider, RabbitMqRetryExceptionPredicatesProvider>();

            builder.Services.AddSingleton<RabbitMqPathBuilder>();

            builder.Services.AddSingleton<IMessagingInfrastructure>(sp =>
            {
                // Folded receiver/dispatcher factory captured directly in this infrastructure's
                // descriptor (NOT resolved from the container by the shared MessagingInfrastructureFactory
                // type), so each broker keeps its own factory under multi-broker registration.
                //
                // SCOPE DIVERGENCE from the SqlServiceBroker fold: that fold open-resolve-and-DISPOSEs a
                // transient scope per Create() call, because its receiver IS a container-published service.
                // Azure Service Bus's receiver now takes the SAME route as RabbitMQ's — constructed via
                // ActivatorUtilities.CreateInstance, not container-published (see
                // ChatterAzureServiceBusExtensions.AddAzureServiceBus) — but it constructs a fresh instance PER
                // Create() call, whereas RabbitMQ's receiver is deliberately NOT published — there is no
                // RabbitMqReceiver descriptor — and is constructed here, once per `AddRabbitMq` registration, at
                // this single site. (A duplicate `AddRabbitMq` call registers a second `IMessagingInfrastructure`
                // descriptor and constructs a second receiver, but that second instance is inert:
                // ActivatorUtilities.CreateInstance does not enlist it for container disposal, and neither
                // MessagingInfrastructure nor MessagingInfrastructureFactory is disposable, so nothing holds a
                // dispose path to it.) Two consequences, both load-bearing:
                //   1. No other scope can obtain a receiver instance, so there is no such thing as a receiver that
                //      was never initialized yet can still reach the shared singleton source's teardown. That
                //      category is what made the receiver's own dispose path need per-instance guarding.
                //   2. The instance outlives this delegate, which it must: the core drives the returned receiver
                //      through InitializeAsync, then StopReceivingAsync, then Dispose, all AFTER the delegate
                //      returns. A per-call resolve-and-dispose scope would hand the core a disposed receiver.
                // RejectMultipleReceivers guarantees at most one RabbitMQ receiver PER REGISTRATION, so one instance
                // is enough for that registration, and every constructor dependency (IRabbitMqConnectionSource,
                // RabbitMqOptions, IBodyConverterFactory, ILogger<>) is a SINGLETON, so root resolution is legal
                // under scope validation. The sender
                // (IMessagingInfrastructureDispatcher, NOT disposable, no Dispose) stays container-published and
                // keeps the dispose-per-call scope shape. The singleton IRabbitMqConnectionSource's own teardown is
                // not this delegate's concern: the container created it, so the root provider disposes it.
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                var receiver = ActivatorUtilities.CreateInstance<RabbitMqReceiver>(sp);
                var infrastructureFactory = new MessagingInfrastructureFactory(
                    () => receiver,
                    () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        return scope.ServiceProvider.GetRequiredService<RabbitMqSender>();
                    });
                var pathBuilder = sp.GetRequiredService<RabbitMqPathBuilder>();
                return new MessagingInfrastructure(RabbitMqMessageContext.InfrastructureType, infrastructureFactory, infrastructureFactory, pathBuilder);
            });

            // Register RabbitMqBodyConverter as an IBrokeredMessageBodyConverter PROVIDER so the core
            // BodyConverterFactory enumerates it and keys it under its ContentType. The sender and receiver no longer
            // depend on the concrete converter — they resolve through IBodyConverterFactory keyed on
            // RabbitMqOptions.MessageBodyType — so no concrete registration is needed.
            builder.Services.AddSingleton<IBrokeredMessageBodyConverter, RabbitMqBodyConverter>();
            builder.Services.AddSingleton(options);

            return builder;
        }

        // Fails fast at registration when FullAtomicityViaInfrastructure is configured for RabbitMQ, on either the
        // global MessageBrokerOptions.TransactionMode or any RabbitMQ-attributed discovered receiver's per-call
        // mode. Both surfaces are read off the EFFECTIVE IServiceCollection registration (the MessageBrokerOptions
        // and the IDiscoveredReceiverRegistry are each registered as a singleton ImplementationInstance during core
        // configuration, which runs before AddRabbitMq), so no provider is built here.
        private static void RejectFullAtomicity(IServiceCollection services)
        {
            var globalMode = EffectiveRegistration(services, typeof(MessageBrokerOptions))?
                .ImplementationInstance as MessageBrokerOptions;
            if (globalMode?.TransactionMode == TransactionMode.FullAtomicityViaInfrastructure)
            {
                throw new NotSupportedException(FullAtomicityMessage);
            }

            var discoveredRegistry = EffectiveDiscoveredReceiverRegistry(services);
            if (discoveredRegistry is null)
            {
                return;
            }

            // A blank/empty ReceiverOptions.InfrastructureType resolves to the FIRST-REGISTERED
            // IMessagingInfrastructure at runtime. This runs BEFORE AddRabbitMq registers RabbitMQ's own
            // IMessagingInfrastructure descriptor, so RabbitMQ is the core default only when no earlier broker
            // registered one — mirroring the Azure Service Bus attribution.
            var rabbitMqIsDefault = !AnyRegistration(services, typeof(IMessagingInfrastructure));

            foreach (var receiverOptions in discoveredRegistry.DiscoveredReceivers)
            {
                if (!IsRabbitMqReceiver(receiverOptions.InfrastructureType, rabbitMqIsDefault))
                {
                    continue;
                }

                if (receiverOptions.TransactionMode == TransactionMode.FullAtomicityViaInfrastructure)
                {
                    throw new NotSupportedException(FullAtomicityMessage);
                }
            }
        }

        // Fails fast at registration when MORE THAN ONE RabbitMQ-attributed receiver is discovered. The singleton
        // connection source owns one receive channel and one registration delegate, so a second receiver clobbers the
        // first and recovery only re-registers the last. Uses the SAME effective-registration read (no provider built)
        // and the SAME RabbitMQ attribution (IsRabbitMqReceiver + the first-registered-IMessagingInfrastructure
        // default rule) as RejectFullAtomicity. No-op when the registry is absent or has 0/1 RabbitMQ receivers.
        private static void RejectMultipleReceivers(IServiceCollection services)
        {
            var discoveredRegistry = EffectiveDiscoveredReceiverRegistry(services);
            if (discoveredRegistry is null)
            {
                return;
            }

            var rabbitMqIsDefault = !AnyRegistration(services, typeof(IMessagingInfrastructure));

            var rabbitMqReceiverCount = discoveredRegistry.DiscoveredReceivers
                .Count(receiverOptions => IsRabbitMqReceiver(receiverOptions.InfrastructureType, rabbitMqIsDefault));

            if (rabbitMqReceiverCount > 1)
            {
                throw new NotSupportedException(MultipleReceiversMessage);
            }
        }

        // Fails fast at registration when a consumer registered its own IRabbitMqConnectionSource at a lifetime
        // other than Singleton. AddIfNotRegistered preserves a pre-existing descriptor as-is, so without this guard
        // a Scoped or Transient override survives registration and is then resolved from the ROOT provider by the
        // IMessagingInfrastructure factory: under host scope validation that throws "Cannot resolve scoped service
        // from root provider" at first resolution, and without validation the instance is root-captured for the
        // application's lifetime — silently breaking the one-IConnection-per-process invariant. Read off the
        // EFFECTIVE IServiceCollection registration (no provider built), matching the other two guards: the lifetime
        // that matters is the one belonging to the descriptor the container will actually resolve. No-op when no
        // override exists (AddRabbitMq registers the singleton itself) or when the override is already a Singleton.
        private static void RejectNonSingletonConnectionSourceOverride(IServiceCollection services)
        {
            var existing = EffectiveRegistration(services, typeof(IRabbitMqConnectionSource));
            if (existing is null || existing.Lifetime == ServiceLifetime.Singleton)
            {
                return;
            }

            throw new NotSupportedException(string.Format(NonSingletonConnectionSourceMessage, existing.Lifetime));
        }

        // The descriptor a SINGLE-service request resolves to. Microsoft DI resolves such a request from the LAST
        // registered descriptor for the service type (CallSiteFactory.TryCreateExact forwards to descriptor.Last),
        // so a later registration SUPERSEDES an earlier one. Every registration guard that asks "what is registered
        // for this service type" must ask it through here: reading the FIRST match reasons about a descriptor the
        // container may never hand anyone, which both lets an unsupportable effective registration boot and rejects
        // a supported one.
        private static ServiceDescriptor EffectiveRegistration(IServiceCollection services, Type serviceType)
            => services.LastOrDefault(descriptor => descriptor.ServiceType == serviceType);

        // Whether ANY descriptor exists for the service type. This is the correct — and only meaningful — question
        // for a service resolved as IEnumerable<T>, which captures EVERY matching descriptor in registration order
        // rather than just one. IMessagingInfrastructure is exactly that: MessagingInfrastructureProvider takes the
        // whole enumerable and makes FirstOrDefault() the default broker, so an earlier-registered infrastructure
        // owns the default and no single descriptor is "the" registration. Never convert these callers to
        // EffectiveRegistration — last-wins would be wrong here, not merely different.
        private static bool AnyRegistration(IServiceCollection services, Type serviceType)
            => services.Any(descriptor => descriptor.ServiceType == serviceType);

        // The registry the container will resolve, or null when none is registered or the effective descriptor is
        // not an ImplementationInstance (a type- or factory-registered registry cannot be read without building a
        // provider, so both receiver guards no-op on it).
        private static IDiscoveredReceiverRegistry EffectiveDiscoveredReceiverRegistry(IServiceCollection services)
            => EffectiveRegistration(services, typeof(IDiscoveredReceiverRegistry))?
                .ImplementationInstance as IDiscoveredReceiverRegistry;

        // A RabbitMQ receiver is one EXPLICITLY typed to RabbitMQ (always claimed) OR one left on the default
        // infrastructure (blank/empty InfrastructureType) ONLY WHEN RabbitMQ is the core's resolved default.
        private static bool IsRabbitMqReceiver(string infrastructureType, bool rabbitMqIsDefault)
            => (rabbitMqIsDefault && string.IsNullOrWhiteSpace(infrastructureType))
            || string.Equals(infrastructureType, RabbitMqMessageContext.InfrastructureType, StringComparison.Ordinal);

        private const string FullAtomicityMessage =
            "RabbitMQ does not support TransactionMode.FullAtomicityViaInfrastructure: there is no atomic "
            + "receive-and-send across the consume and a downstream publish. Use TransactionMode.None or "
            + "TransactionMode.ReceiveOnly, and the Outbox for transactional send.";

        private const string NonSingletonConnectionSourceMessage =
            "A custom IRabbitMqConnectionSource must be registered as a SINGLETON, but one is registered as {0}. "
            + "RabbitMQ owns ONE IConnection per process — its serialized receive channel and pooled publish "
            + "channels are process-wide and must not be re-created per scope — and the receiver is constructed "
            + "from the root provider, which cannot resolve a scoped or transient source. Register the override "
            + "with ServiceLifetime.Singleton before calling AddRabbitMq.";

        private const string MultipleReceiversMessage =
            "RabbitMQ supports only a single queue receiver per process: the connection source owns one receive "
            + "channel and one consumer registration, so a second receiver would clobber the first and recovery "
            + "would re-register only the last. A single RabbitMQ queue receiver per process is a 0.1.0 limitation; "
            + "full multi-receiver support is tracked in https://github.com/brenpike/Chatter/issues/195.";
    }
}
