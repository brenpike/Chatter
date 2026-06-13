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
                // type), so each broker keeps its own factory under multi-broker registration. Each
                // delegate opens a DI scope, resolves the scoped infrastructure service, and disposes the
                // scope — mirroring the SqlServiceBroker / Azure Service Bus folds exactly.
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                var infrastructureFactory = new MessagingInfrastructureFactory(
                    () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        return scope.ServiceProvider.GetRequiredService<RabbitMqReceiver>();
                    },
                    () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        return scope.ServiceProvider.GetRequiredService<RabbitMqSender>();
                    });
                var pathBuilder = sp.GetRequiredService<RabbitMqPathBuilder>();
                return new MessagingInfrastructure(RabbitMqMessageContext.InfrastructureType, infrastructureFactory, infrastructureFactory, pathBuilder);
            });

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

        // A RabbitMQ receiver is one EXPLICITLY typed to RabbitMQ (always claimed) OR one left on the default
        // infrastructure (blank/empty InfrastructureType) ONLY WHEN RabbitMQ is the core's resolved default.
        private static bool IsRabbitMqReceiver(string infrastructureType, bool rabbitMqIsDefault)
            => (rabbitMqIsDefault && string.IsNullOrWhiteSpace(infrastructureType))
            || string.Equals(infrastructureType, RabbitMqMessageContext.InfrastructureType, StringComparison.Ordinal);

        private const string FullAtomicityMessage =
            "RabbitMQ does not support TransactionMode.FullAtomicityViaInfrastructure: there is no atomic "
            + "receive-and-send across the consume and a downstream publish. Use TransactionMode.None or "
            + "TransactionMode.ReceiveOnly, and the Outbox for transactional send.";
    }
}
