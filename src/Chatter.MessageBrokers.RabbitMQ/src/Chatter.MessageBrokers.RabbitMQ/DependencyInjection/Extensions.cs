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

            // INVARIANT: one IConnection per process — IRabbitMqConnectionSource is a SINGLETON. This is the one
            // deliberate lifetime divergence from the SqlServiceBroker fold (whose ISqlConnectionSource is Scoped):
            // the AMQP connection, its serialized receive channel, and its pooled publish channels are process-wide
            // and must not be re-created per scope.
            builder.Services.AddIfNotRegistered<IRabbitMqConnectionSource, RabbitMqConnectionSource>(ServiceLifetime.Singleton);

            builder.Services.AddIfNotRegistered<RabbitMqReceiver>(ServiceLifetime.Scoped);
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
                // LIFETIME DIVERGENCE from the SqlServiceBroker / Azure Service Bus folds: those folds
                // open-resolve-and-DISPOSE a transient scope per Create() call because their scoped receiver's
                // Dispose is a no-op for the (Scoped) connection source. RabbitMQ deliberately makes
                // IRabbitMqConnectionSource a SINGLETON (one IConnection per process), and RabbitMqReceiver
                // (IMessagingInfrastructureReceiver : IDisposable) ESCALATES its Dispose to the singleton
                // source's FULL teardown. A per-call `using var scope` would therefore dispose the returned
                // receiver — and with it the shared singleton source — before InitializeAsync ever runs,
                // so receiver startup would get back an already-disposed source and throw
                // ObjectDisposedException. The receiver scope must instead live for the infrastructure's
                // (singleton) lifetime. RejectMultipleReceivers guarantees at most one RabbitMQ receiver, so a
                // single long-lived scope created once here is correct. The sender (IMessagingInfrastructureDispatcher,
                // NOT disposable, no Dispose) is unaffected, so its delegate keeps the dispose-per-call shape.
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                var receiverScope = scopeFactory.CreateScope();
                var infrastructureFactory = new MessagingInfrastructureFactory(
                    () => receiverScope.ServiceProvider.GetRequiredService<RabbitMqReceiver>(),
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
            builder.Services.AddScoped<IBrokeredMessageBodyConverter, RabbitMqBodyConverter>();
            builder.Services.AddSingleton(options);

            return builder;
        }

        // Fails fast at registration when FullAtomicityViaInfrastructure is configured for RabbitMQ, on either the
        // global MessageBrokerOptions.TransactionMode or any RabbitMQ-attributed discovered receiver's per-call
        // mode. Both surfaces are read directly off the IServiceCollection (the MessageBrokerOptions and the
        // IDiscoveredReceiverRegistry are each registered as a singleton ImplementationInstance during core
        // configuration, which runs before AddRabbitMq), so no provider is built here.
        private static void RejectFullAtomicity(IServiceCollection services)
        {
            var globalMode = services
                .FirstOrDefault(d => d.ServiceType == typeof(MessageBrokerOptions))?
                .ImplementationInstance as MessageBrokerOptions;
            if (globalMode?.TransactionMode == TransactionMode.FullAtomicityViaInfrastructure)
            {
                throw new NotSupportedException(FullAtomicityMessage);
            }

            var discoveredRegistry = services
                .FirstOrDefault(d => d.ServiceType == typeof(IDiscoveredReceiverRegistry))?
                .ImplementationInstance as IDiscoveredReceiverRegistry;
            if (discoveredRegistry is null)
            {
                return;
            }

            // A blank/empty ReceiverOptions.InfrastructureType resolves to the FIRST-REGISTERED
            // IMessagingInfrastructure at runtime. This runs BEFORE AddRabbitMq registers RabbitMQ's own
            // IMessagingInfrastructure descriptor, so RabbitMQ is the core default only when no earlier broker
            // registered one — mirroring the Azure Service Bus attribution.
            var rabbitMqIsDefault = !services.Any(d => d.ServiceType == typeof(IMessagingInfrastructure));

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
        // first and recovery only re-registers the last. Uses the SAME IServiceCollection-read (no provider built) and
        // the SAME RabbitMQ attribution (IsRabbitMqReceiver + the first-registered-IMessagingInfrastructure default
        // rule) as RejectFullAtomicity. No-op when the registry is absent or has 0/1 RabbitMQ receivers.
        private static void RejectMultipleReceivers(IServiceCollection services)
        {
            var discoveredRegistry = services
                .FirstOrDefault(d => d.ServiceType == typeof(IDiscoveredReceiverRegistry))?
                .ImplementationInstance as IDiscoveredReceiverRegistry;
            if (discoveredRegistry is null)
            {
                return;
            }

            var rabbitMqIsDefault = !services.Any(d => d.ServiceType == typeof(IMessagingInfrastructure));

            var rabbitMqReceiverCount = discoveredRegistry.DiscoveredReceivers
                .Count(receiverOptions => IsRabbitMqReceiver(receiverOptions.InfrastructureType, rabbitMqIsDefault));

            if (rabbitMqReceiverCount > 1)
            {
                throw new NotSupportedException(MultipleReceiversMessage);
            }
        }

        // A RabbitMQ receiver is one EXPLICITLY typed to RabbitMQ (always claimed) OR one left on the default
        // infrastructure (blank/empty InfrastructureType) ONLY WHEN RabbitMQ is the core's resolved default.
        private static bool IsRabbitMqReceiver(string infrastructureType, bool rabbitMqIsDefault)
            => (rabbitMqIsDefault && string.IsNullOrWhiteSpace(infrastructureType))
            || string.Equals(infrastructureType, RabbitMqMessageContext.InfrastructureType, StringComparison.Ordinal);

        private const string FullAtomicityMessage =
            "RabbitMQ does not support TransactionMode.FullAtomicityViaInfrastructure: there is no atomic "
            + "receive-and-send across the consume and a downstream publish. Use TransactionMode.None or "
            + "TransactionMode.ReceiveOnly, and the Outbox for transactional send.";

        private const string MultipleReceiversMessage =
            "RabbitMQ supports only a single queue receiver per process: the connection source owns one receive "
            + "channel and one consumer registration, so a second receiver would clobber the first and recovery "
            + "would re-register only the last. A single RabbitMQ queue receiver per process is a 0.1.0 limitation; "
            + "full multi-receiver support is tracked in https://github.com/brenpike/Chatter/issues/195.";
    }
}
