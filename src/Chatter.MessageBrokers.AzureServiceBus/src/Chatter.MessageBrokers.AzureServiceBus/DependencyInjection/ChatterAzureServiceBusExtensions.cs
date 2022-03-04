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
            builder.Services.AddSingleton<ServiceBusReceiverFactory>();

            builder.Services.AddScoped<ServiceBusMessageSender>();
            builder.Services.AddSingleton<ServiceBusMessageSenderFactory>();

            builder.Services.AddSingleton<BrokeredMessageSenderPool>();
            builder.Services.AddSingleton<AzureServiceBusEntityPathBuilder>();

            builder.Services.AddSingleton<ICircuitBreakerExceptionPredicatesProvider, ServiceBusCircuitBreakerExceptionPredicatesProvider>();
            builder.Services.AddSingleton<IRetryExceptionPredicatesProvider, ServiceBusRetryExceptionPredicatesProvider>();

            builder.Services.AddSingleton<IMessagingInfrastructure>(sp =>
            {
                var sender = sp.GetRequiredService<ServiceBusMessageSenderFactory>();
                var receiver = sp.GetRequiredService<ServiceBusReceiverFactory>();
                var pathBuilder = sp.GetRequiredService<AzureServiceBusEntityPathBuilder>();
                return new MessagingInfrastructure(ASBMessageContext.InfrastructureType, receiver, sender, pathBuilder);
            });

            return builder;
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
