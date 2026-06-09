using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.DependencyInjection;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Receiving.CircuitBreaker;
using Chatter.MessageBrokers.AzureServiceBus.Receiving.Retry;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using System;
using System.Collections.Generic;
using System.Linq;
using ServiceBusClient = Azure.Messaging.ServiceBus.ServiceBusClient;
using ServiceBusClientOptions = Azure.Messaging.ServiceBus.ServiceBusClientOptions;
using ServiceBusConnectionStringProperties = Azure.Messaging.ServiceBus.ServiceBusConnectionStringProperties;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ChatterAzureServiceBusExtensions
    {
        /// <summary>
        /// Adds Azure Service Bus as messaging infrastructure for Chatter.MessageBrokers. <see cref="ServiceBusOptions"/> configured via configuration.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configSectionName"></param>
        /// <returns></returns>
        public static IChatterBuilder AddAzureServiceBus(this IChatterBuilder builder, Action<ServiceBusOptionsBuilder> optionsBuilder = null)
        {
            var optBuilder = ServiceBusOptionsBuilder.Create(builder.Services, builder.Configuration);
            optionsBuilder?.Invoke(optBuilder);
            var options = optBuilder.Build();
            return AddAzureServiceBus(builder, options);
        }

        private static IChatterBuilder AddAzureServiceBus(this IChatterBuilder builder, ServiceBusOptions options)
        {
            builder.Services.AddScoped<ServiceBusReceiver>();
            builder.Services.AddScoped<ServiceBusMessageSender>();

            // Ensure the receiver registry exists even when no receivers were configured (e.g. a send-only
            // host), so the shared-client factory can always resolve it. AddQueueReceiver/AddTopicSubscription
            // run before this (the options delegate completes before AddAzureServiceBus(builder, options)),
            // so any populated registry is reused here rather than replaced.
            GetOrAddReceiverRegistry(builder.Services);

            // INVARIANT: a single shared ServiceBusClient per namespace. The client is a singleton built once
            // from ServiceBusOptions; receivers and senders are created off this one client. The client is
            // registered via a factory delegate (not as a pre-built implementation instance) so the container
            // OWNS it: Microsoft DI disposes only the singletons it creates, so factory registration ensures
            // the client — and the senders cached off it — are disposed on provider teardown (e.g. when
            // apps/tests dispose and rebuild the service provider).
            //
            // The factory reads the receiver registry at lazy build-time (after all AddQueueReceiver/
            // AddTopicSubscription calls have run, before the first hosted-service pump starts) to compute the
            // effective cross-entity-transactions flag and to enforce the single-top-level-entity guard. It
            // reads ReceiverOptions FROM THE REGISTRY, never re-entering DI to resolve receivers.
            builder.Services.AddSingleton(sp =>
            {
                var registry = sp.GetRequiredService<ServiceBusReceiverRegistry>();
                var globalTransactionMode = sp.GetRequiredService<MessageBrokerOptions>().TransactionMode;
                var enableCrossEntityTransactions = ResolveEffectiveCrossEntityTransactions(options, registry, globalTransactionMode);
                return CreateSharedClient(options, enableCrossEntityTransactions);
            });
            builder.Services.AddSingleton<IServiceBusMessageSenderFactory>(sp =>
                new AzureSdkMessageSenderFactory(sp.GetRequiredService<ServiceBusClient>()));
            builder.Services.AddSingleton<AzureServiceBusEntityPathBuilder>();

            builder.Services.AddSingleton<ICircuitBreakerExceptionPredicatesProvider, ServiceBusCircuitBreakerExceptionPredicatesProvider>();
            builder.Services.AddSingleton<IRetryExceptionPredicatesProvider, ServiceBusRetryExceptionPredicatesProvider>();

            builder.Services.AddSingleton<IMessagingInfrastructure>(sp =>
            {
                // Folded receiver/dispatcher factory captured directly in this infrastructure's
                // descriptor (NOT resolved from the container by the shared MessagingInfrastructureFactory
                // type), so each broker keeps its own factory under multi-broker registration. Each
                // delegate opens a DI scope, resolves the scoped infrastructure service, and disposes the
                // scope — reproducing the former ServiceBusReceiverFactory / ServiceBusMessageSenderFactory
                // behavior exactly.
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                var infrastructureFactory = new MessagingInfrastructureFactory(
                    () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        return scope.ServiceProvider.GetRequiredService<ServiceBusReceiver>();
                    },
                    () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        return scope.ServiceProvider.GetRequiredService<ServiceBusMessageSender>();
                    });
                var pathBuilder = sp.GetRequiredService<AzureServiceBusEntityPathBuilder>();
                return new MessagingInfrastructure(ASBMessageContext.InfrastructureType, infrastructureFactory, infrastructureFactory, pathBuilder);
            });

            return builder;
        }

        // Computes the EFFECTIVE cross-entity-transactions flag for the shared client and enforces the
        // single-top-level-entity startup guard. Cross-entity is ON when explicitly opted in via
        // ServiceBusOptions OR when any registered receiver's EFFECTIVE transaction mode is
        // FullAtomicityViaInfrastructure (which depends on it). A receiver with no per-call mode inherits the
        // global MessageBrokerOptions.TransactionMode, so globalTransactionMode is folded into that
        // per-receiver effective-mode computation. When ON, Azure Service Bus pins the client to ONE
        // top-level entity, so registering more than one distinct top-level receiver entity (queue name /
        // topic name; subscriptions on the same topic count once) is unsupportable — fail fast and loud at
        // startup rather than letting the SDK silently fail the second receiver.
        private static bool ResolveEffectiveCrossEntityTransactions(ServiceBusOptions options, ServiceBusReceiverRegistry registry, TransactionMode globalTransactionMode)
        {
            var effective = options.EnableCrossEntityTransactions || registry.AnyRequiresCrossEntityTransactions(globalTransactionMode);
            if (!effective)
            {
                return false;
            }

            var distinctTopLevelEntities = registry.DistinctTopLevelEntities();
            if (distinctTopLevelEntities.Count > 1)
            {
                var entityList = string.Join(", ", distinctTopLevelEntities);
                throw new InvalidOperationException(
                    $"Azure Service Bus cross-entity transactions support only a single top-level receiver entity per host; found: {entityList}. Disable cross-entity (default) or consolidate receivers.");
            }

            return true;
        }

        // Builds the single shared ServiceBusClient for the namespace from ServiceBusOptions. A null
        // TokenCredential means SAS auth via the connection string; a non-null TokenCredential authenticates
        // against the fully-qualified namespace derived from the connection string's Endpoint. Cross-entity
        // transactions are enabled on the client (per the resolved effective flag) so the send + the
        // receiver's settle enlist in one transaction, and the configured RetryOptions (when present) are
        // carried onto the client.
        private static ServiceBusClient CreateSharedClient(ServiceBusOptions options, bool enableCrossEntityTransactions)
        {
            var clientOptions = new ServiceBusClientOptions
            {
                EnableCrossEntityTransactions = enableCrossEntityTransactions,
            };
            if (options.RetryOptions != null)
            {
                clientOptions.RetryOptions = options.RetryOptions;
            }

            if (options.TokenCredential is null)
            {
                return new ServiceBusClient(options.ConnectionString, clientOptions);
            }

            var fullyQualifiedNamespace = ServiceBusConnectionStringProperties.Parse(options.ConnectionString).FullyQualifiedNamespace;
            return new ServiceBusClient(fullyQualifiedNamespace, options.TokenCredential, clientOptions);
        }

        public static ServiceBusOptionsBuilder AddTopicSubscription<TMessage>(this ServiceBusOptionsBuilder builder,
                                                                              string topicName,
                                                                              string subscriptionName,
                                                                              string errorQueuePath = null,
                                                                              string description = null,
                                                                              TransactionMode? transactionMode = null,
                                                                              int maxReceiveAttempts = 10)
            where TMessage : class, IEvent
        {
            // The TOPIC is the top-level entity Azure Service Bus pins a cross-entity transaction to; two
            // subscriptions on the same topic share one top-level entity.
            GetOrAddReceiverRegistry(builder.Services).Register(topicName, transactionMode);
            builder.Services.AddReceiver<TMessage>(subscriptionName, errorQueuePath, description, topicName, transactionMode, ASBMessageContext.InfrastructureType, maxReceiveAttempts: maxReceiveAttempts);
            return builder;
        }

        public static ServiceBusOptionsBuilder AddQueueReceiver<TMessage>(this ServiceBusOptionsBuilder builder,
                                                                          string queueName,
                                                                          string errorQueuePath = null,
                                                                          string description = null,
                                                                          TransactionMode? transactionMode = null,
                                                                          int maxReceiveAttempts = 10)
            where TMessage : class, ICommand
        {
            // The QUEUE is itself the top-level entity Azure Service Bus pins a cross-entity transaction to.
            GetOrAddReceiverRegistry(builder.Services).Register(queueName, transactionMode);
            builder.Services.AddReceiver<TMessage>(queueName, errorQueuePath, description, queueName, transactionMode, ASBMessageContext.InfrastructureType, maxReceiveAttempts: maxReceiveAttempts);
            return builder;
        }

        // Resolves the single ServiceBusReceiverRegistry instance shared across all AddQueueReceiver/
        // AddTopicSubscription calls and the shared-client factory, registering it on first use. The instance
        // is captured directly into the singleton descriptor so the same object is both written here (at
        // registration) and read by the client factory (at lazy resolve).
        private static ServiceBusReceiverRegistry GetOrAddReceiverRegistry(IServiceCollection services)
        {
            var existing = services.FirstOrDefault(d => d.ServiceType == typeof(ServiceBusReceiverRegistry));
            if (existing?.ImplementationInstance is ServiceBusReceiverRegistry registry)
            {
                return registry;
            }

            registry = new ServiceBusReceiverRegistry();
            services.AddSingleton(registry);
            return registry;
        }
    }
}
