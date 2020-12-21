using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
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
            builder.Services.AddSingleton<IMessagingInfrastructureReceiverFactory, ServiceBusReceiverFactory>();

            builder.Services.AddScoped<ServiceBusMessageSender>();
            builder.Services.AddSingleton<IMessagingInfrastructureDispatcherFactory, ServiceBusMessageSenderFactory>();

            builder.Services.AddScoped<IMessagingInfrastructure>(sp =>
            {
                var sender = sp.GetRequiredService<IMessagingInfrastructureDispatcherFactory>();
                var receiver = sp.GetRequiredService<IMessagingInfrastructureReceiverFactory>();
                return new MessagingInfrastructure(ASBMessageContext.InfrastructureType, receiver, sender);
            });


            builder.Services.AddSingleton<BrokeredMessageSenderPool>();
            builder.Services.Replace<IBrokeredMessagePathBuilder, AzureServiceBusEntityPathBuilder>(ServiceLifetime.Scoped);

            return builder;
        }
    }
}
