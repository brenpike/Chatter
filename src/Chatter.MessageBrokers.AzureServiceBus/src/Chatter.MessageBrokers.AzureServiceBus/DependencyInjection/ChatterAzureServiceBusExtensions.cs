using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Receiving.CircuitBreaker;
using Chatter.MessageBrokers.AzureServiceBus.Receiving.Retry;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using System;
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

            // INVARIANT: a single shared ServiceBusClient per namespace. Cross-entity transactions require
            // one client per namespace, so the client is a singleton built once from ServiceBusOptions with
            // EnableCrossEntityTransactions enabled; receivers and senders are created off this one client.
            // The client is registered via a factory delegate (not as a pre-built implementation instance) so
            // the container OWNS it: Microsoft DI disposes only the singletons it creates, so factory
            // registration ensures the client — and the senders cached off it — are disposed on provider
            // teardown (e.g. when apps/tests dispose and rebuild the service provider).
            builder.Services.AddSingleton(_ => CreateSharedClient(options));
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

        // Builds the single shared ServiceBusClient for the namespace from ServiceBusOptions. A null
        // TokenCredential means SAS auth via the connection string; a non-null TokenCredential authenticates
        // against the fully-qualified namespace derived from the connection string's Endpoint. Cross-entity
        // transactions are enabled on the client so the send + the receiver's settle enlist in one
        // transaction, and the configured RetryOptions (when present) are carried onto the client.
        private static ServiceBusClient CreateSharedClient(ServiceBusOptions options)
        {
            var clientOptions = new ServiceBusClientOptions
            {
                EnableCrossEntityTransactions = true,
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
            builder.Services.AddReceiver<TMessage>(queueName, errorQueuePath, description, queueName, transactionMode, ASBMessageContext.InfrastructureType, maxReceiveAttempts: maxReceiveAttempts);
            return builder;
        }
    }
}
