using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Receiving;
using Microsoft.Extensions.Configuration;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ChatterAzureServiceBusExtensions
    {
        public static ServiceBusOptionsBuilder AddAzureServiceBus(this IServiceCollection services, IConfiguration configuration)
            => new ServiceBusOptionsBuilder(services, configuration);

        /// <summary>
        /// Adds registrations required for Azure Service Bus integration with Chatter.CQRS, including <see cref="ServiceBusOptions"/>, queue, topic and subscription factories and publishers.
        /// </summary>
        /// <param name="builder">The singleton <see cref="ChatterBuilder"/> instance used for registration.</param>
        /// <param name="configSectionName">The configuration section name containing Azure Service Bus configuration values used to build a <see cref="ServiceBusOptions"/> instance. 'ServiceBus' is the default value.</param>
        /// <returns>The singleton <see cref="IChatterBuilder"/> instance.</returns>
        public static IChatterBuilder AddAzureServiceBus(this IChatterBuilder builder, Action<ServiceBusOptionsBuilder> optionsBuilder = null)
        {
            var optBuilder = builder.Services.AddAzureServiceBus(builder.Configuration);
            optionsBuilder?.Invoke(optBuilder);
            var options = optBuilder.Build();

            builder.Services.AddScoped<ServiceBusReceiver>();
            builder.Services.AddSingleton<ServiceBusReceiverFactory>();

            builder.Services.AddScoped<ServiceBusMessageSender>();
            builder.Services.AddSingleton<ServiceBusMessageSenderFactory>();

            builder.Services.AddSingleton<BrokeredMessageSenderPool>();
            builder.Services.AddSingleton<AzureServiceBusEntityPathBuilder>();

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
                                                                              TransactionMode? transactionMode = null)
            where TMessage : class, IEvent
        {
            builder.Services.AddReceiver<TMessage>(subscriptionName, errorQueuePath, description, topicName, transactionMode, ASBMessageContext.InfrastructureType);
            return builder;
        }

        public static ServiceBusOptionsBuilder AddQueueReceiver<TMessage>(this ServiceBusOptionsBuilder builder,
                                                                          string queueName,
                                                                          string errorQueuePath = null,
                                                                          string description = null,
                                                                          TransactionMode? transactionMode = null)
            where TMessage : class, ICommand
        {
            builder.Services.AddReceiver<TMessage>(queueName, errorQueuePath, description, queueName, transactionMode, ASBMessageContext.InfrastructureType);
            return builder;
        }
    }
}
