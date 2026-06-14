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
            var receiverRegistry = GetOrAddReceiverRegistry(builder.Services);

            // Fold attribute-registered receivers ([BrokeredMessageAttribute] assembly scan) into the ASB
            // receiver registry and stamp the global MaxConcurrentCalls. This runs at registration time, before
            // the shared-client factory reads the registry at lazy resolve. `options` (the built ServiceBusOptions)
            // is the only place the finalized global MaxConcurrentCalls is known — it is resolved at
            // optBuilder.Build(), AFTER the receiver-registration options delegate has already run.
            PopulateFromDiscoveredReceivers(builder.Services, receiverRegistry, options);

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
            // subscriptions on the same topic share one top-level entity. The SUBSCRIPTION name is the
            // receiver path that distinguishes this receiver from another subscription on the same topic for
            // the per-receiver session lookup.
            GetOrAddReceiverRegistry(builder.Services).Register(topicName, subscriptionName, transactionMode);
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
            // The QUEUE is itself the top-level entity Azure Service Bus pins a cross-entity transaction to,
            // and is its own receiver path for the per-receiver session lookup.
            GetOrAddReceiverRegistry(builder.Services).Register(queueName, queueName, transactionMode);
            builder.Services.AddReceiver<TMessage>(queueName, errorQueuePath, description, queueName, transactionMode, ASBMessageContext.InfrastructureType, maxReceiveAttempts: maxReceiveAttempts);
            return builder;
        }

        // Session-mode sibling of AddTopicSubscription: registers a session-enabled topic subscription. Mirrors
        // AddTopicSubscription exactly, marking the receiver session-mode in the registry (RequiresSession=true)
        // so ServiceBusReceiver.CreateProductionReceiver selects the session adapter for the topic.
        public static ServiceBusOptionsBuilder AddSessionTopicSubscription<TMessage>(this ServiceBusOptionsBuilder builder,
                                                                                     string topicName,
                                                                                     string subscriptionName,
                                                                                     string errorQueuePath = null,
                                                                                     string description = null,
                                                                                     TransactionMode? transactionMode = null,
                                                                                     int maxReceiveAttempts = 10)
            where TMessage : class, IEvent
        {
            // The TOPIC is the top-level entity Azure Service Bus pins a cross-entity transaction to; two
            // subscriptions on the same topic share one top-level entity. The SUBSCRIPTION name is the
            // receiver path that marks THIS subscription session-mode without affecting a sibling normal
            // subscription on the same topic.
            GetOrAddReceiverRegistry(builder.Services).Register(topicName, subscriptionName, transactionMode, requiresSession: true);
            builder.Services.AddReceiver<TMessage>(subscriptionName, errorQueuePath, description, topicName, transactionMode, ASBMessageContext.InfrastructureType, maxReceiveAttempts: maxReceiveAttempts);
            return builder;
        }

        // Session-mode sibling of AddQueueReceiver: registers a session-enabled queue receiver. Mirrors
        // AddQueueReceiver exactly, marking the receiver session-mode in the registry (RequiresSession=true) so
        // ServiceBusReceiver.CreateProductionReceiver selects the session adapter for the queue.
        public static ServiceBusOptionsBuilder AddSessionQueueReceiver<TMessage>(this ServiceBusOptionsBuilder builder,
                                                                                 string queueName,
                                                                                 string errorQueuePath = null,
                                                                                 string description = null,
                                                                                 TransactionMode? transactionMode = null,
                                                                                 int maxReceiveAttempts = 10)
            where TMessage : class, ICommand
        {
            // The QUEUE is itself the top-level entity Azure Service Bus pins a cross-entity transaction to,
            // and is its own receiver path for the per-receiver session lookup.
            GetOrAddReceiverRegistry(builder.Services).Register(queueName, queueName, transactionMode, requiresSession: true);
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

        // Folds attribute-registered receivers (the core [BrokeredMessageAttribute] assembly scan, which never
        // calls AddQueueReceiver/AddTopicSubscription) into the ASB receiver registry so the cross-entity
        // effective-flag computation and single-top-level-entity guard see them, and stamps the global
        // MaxConcurrentCalls onto each ASB receiver's retained ReceiverOptions.
        //
        // The core IDiscoveredReceiverRegistry retains the LIVE ReceiverOptions instances; it is registered as a
        // singleton ImplementationInstance, so it is read directly off the IServiceCollection here at
        // registration time (no provider build) — the same object the receivers' hosted-service closures
        // captured. Stamping MaxConcurrentCalls on a retained instance is therefore visible when
        // BrokeredMessageReceiver reads ReceiverOptions.MaxConcurrentCalls at init.
        //
        // INVARIANT: a blank/empty ReceiverOptions.InfrastructureType is resolved by the core
        // MessagingInfrastructureProvider to the FIRST-REGISTERED IMessagingInfrastructure (its _default =
        // infrastructures.FirstOrDefault()), NOT unconditionally to ASB. This method runs BEFORE ASB registers
        // its OWN IMessagingInfrastructure descriptor (the AddSingleton<IMessagingInfrastructure> in
        // AddAzureServiceBus, below this call), so ANY IMessagingInfrastructure descriptor already present in
        // `services` at this point belongs to an EARLIER-registered broker. If one exists, that earlier broker —
        // not ASB — is the core's default, so blank-typed receivers are NOT ASB's and must not be claimed.
        // `asbIsDefault` reproduces the core's first-registered-wins resolution BY CONSTRUCTION at ASB's own
        // registration time. ACCEPTED BOUNDARY: a consumer that reorders registrations by adding an
        // IMessagingInfrastructure AFTER AddAzureServiceBus would shift the core default away from ASB; this
        // method only sees the descriptors present when AddAzureServiceBus runs.
        private static void PopulateFromDiscoveredReceivers(IServiceCollection services, ServiceBusReceiverRegistry receiverRegistry, ServiceBusOptions options)
        {
            var discoveredRegistry = services
                .FirstOrDefault(d => d.ServiceType == typeof(IDiscoveredReceiverRegistry))?
                .ImplementationInstance as IDiscoveredReceiverRegistry;
            if (discoveredRegistry is null)
            {
                return;
            }

            var asbIsDefault = !services.Any(d => d.ServiceType == typeof(IMessagingInfrastructure));

            foreach (var receiverOptions in discoveredRegistry.DiscoveredReceivers)
            {
                if (!IsAzureServiceBusReceiver(receiverOptions.InfrastructureType, asbIsDefault))
                {
                    continue;
                }

                // (MaxConcurrentCalls) Stamp the finalized global value onto the retained live instance, before
                // any hosted-service pumps. Visible at receiver init via ReceiverOptions.MaxConcurrentCalls.
                receiverOptions.MaxConcurrentCalls = options.MaxConcurrentCalls;

                // (F3) Infer the ASB top-level entity (queue vs topic) using the same SendingPath/ReceiverPath
                // convention as AzureServiceBusEntityPathBuilder: a queue receiver has SendingPath empty or equal
                // to its receiver path (top-level = receiver path); a topic subscription has a distinct
                // SendingPath that is the topic (top-level = SendingPath). Register with per-call transactionMode
                // null so it inherits the global mode in AnyRequiresCrossEntityTransactions, matching how the
                // inline AddQueueReceiver/AddTopicSubscription guard treats no-per-call-mode receivers. The ASB
                // registry de-dups top-level entities case-insensitively (DistinctTopLevelEntities), so a receiver
                // registered BOTH via attribute and explicit AddQueueReceiver/AddTopicSubscription is not
                // double-counted as a distinct top-level entity.
                var topLevelEntity = InferTopLevelEntity(receiverOptions.SendingPath, receiverOptions.MessageReceiverPath);
                receiverRegistry.Register(topLevelEntity, receiverOptions.MessageReceiverPath, receiverOptions.TransactionMode);
            }
        }

        // An ASB receiver is one EXPLICITLY typed to ASB (always claimed) OR one left on the default
        // infrastructure (blank/empty InfrastructureType) ONLY WHEN ASB is the core's resolved default. The core
        // MessagingInfrastructureProvider resolves a blank InfrastructureType to the FIRST-REGISTERED
        // IMessagingInfrastructure, so a blank-typed receiver is ASB's only when no earlier broker registered its
        // own infrastructure first (asbIsDefault == true). When an earlier broker is the default, blank-typed
        // receivers belong to it and are excluded here, matching the runtime's attribution. Receivers typed to a
        // DIFFERENT non-ASB infrastructure are always excluded.
        private static bool IsAzureServiceBusReceiver(string infrastructureType, bool asbIsDefault)
            => (asbIsDefault && string.IsNullOrWhiteSpace(infrastructureType))
            || string.Equals(infrastructureType, ASBMessageContext.InfrastructureType, StringComparison.Ordinal);

        // Mirrors AzureServiceBusEntityPathBuilder's queue-vs-subscription inference: a queue receiver's
        // sending path is empty or equals its receiver path (the queue IS the top-level entity); a topic
        // subscription's sending path is the distinct topic (the TOPIC is the top-level entity).
        private static string InferTopLevelEntity(string sendingPath, string messageReceiverPath)
        {
            if (string.IsNullOrWhiteSpace(sendingPath) || string.Equals(sendingPath, messageReceiverPath, StringComparison.Ordinal))
            {
                return messageReceiverPath;
            }

            return sendingPath;
        }
    }
}
