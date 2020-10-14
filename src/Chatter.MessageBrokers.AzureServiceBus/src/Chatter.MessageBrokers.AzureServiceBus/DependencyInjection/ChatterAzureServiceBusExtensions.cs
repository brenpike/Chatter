﻿using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Microsoft.Azure.ServiceBus.Primitives;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ChatterAzureServiceBusExtensions
    {
        public static ServiceBusOptionsBuilder AddAzureServiceBus(this IServiceCollection services) 
            => new ServiceBusOptionsBuilder(services);

        /// <summary>
        /// Adds registrations required for Azure Service Bus integration with Chatter.CQRS, including <see cref="ServiceBusOptions"/>, queue, topic and subscription factories and publishers.
        /// </summary>
        /// <param name="builder">The singleton <see cref="ChatterBuilder"/> instance used for registration.</param>
        /// <param name="configSectionName">The configuration section name containing Azure Service Bus configuration values used to build a <see cref="ServiceBusOptions"/> instance. 'ServiceBus' is the default value.</param>
        /// <returns>The singleton <see cref="IChatterBuilder"/> instance.</returns>
        public static IChatterBuilder AddAzureServiceBus(this IChatterBuilder builder, Action<ServiceBusOptionsBuilder> optionsBuilder)
        {
            var optBuilder = builder.Services.AddAzureServiceBus();
            optionsBuilder(optBuilder);
            optBuilder.Build();

            return AddAzureServiceBus(builder);
        }

        private static IChatterBuilder AddAzureServiceBus(IChatterBuilder builder)
        {
            builder.Services.AddScoped<IBrokeredMessageDetailProvider, BrokeredMessageAttributeProvider>();
            builder.Services.AddScoped<IMessagingInfrastructureDispatcher, ServiceBusMessageSender>();
            builder.Services.AddSingleton<BrokeredMessageSenderPool>();
            builder.Services.AddScoped<IMessagingInfrastructureReceiver, ServiceBusReceiver>();

            return builder;
        }
    }
}
