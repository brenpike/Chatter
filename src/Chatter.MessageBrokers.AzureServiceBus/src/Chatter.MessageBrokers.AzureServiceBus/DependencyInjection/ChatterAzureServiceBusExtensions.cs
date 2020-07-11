using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using Scrutor;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ChatterAzureServiceBusExtensions
    {
        public static ServiceBusOptionsBuilder AddAzureServiceBus(this IServiceCollection services)
        {
            return new ServiceBusOptionsBuilder(services);
        }

        /// <summary>
        /// Adds registrations required for Azure Service Bus integration with Chatter.CQRS, including <see cref="ServiceBusOptions"/>, queue, topic and subscription factories and publishers.
        /// </summary>
        /// <param name="builder">The singleton <see cref="ChatterBuilder"/> instance used for registration.</param>
        /// <param name="configSectionName">The configuration section name containing Azure Service Bus configuration values used to build a <see cref="ServiceBusOptions"/> instance. 'ServiceBus' is the default value.</param>
        /// <returns>The singleton <see cref="IChatterBuilder"/> instance.</returns>
        public static IChatterBuilder AddAzureServiceBus(this IChatterBuilder builder, Action<ServiceBusOptionsBuilder> optionsBuilder)
        {
            optionsBuilder(builder.Services.AddAzureServiceBus());
            return AddAzureServiceBus(builder);
        }

        private static IChatterBuilder AddAzureServiceBus(IChatterBuilder builder)
        {
            builder.Services.AddSingleton<IBrokeredMessageDetailProvider, BrokeredMessageAttributeProvider>();
            builder.Services.AddSingleton<IBrokeredMessageDispatcher, MessageDispatcher>();
            builder.Services.AddSingleton<BrokeredMessageSenderPool>();
            //builder.Services.AddTransient<ICompensationStrategy, DeadLetterCompensationStrategy>();
            builder.Services.AddTransient<ICompensationRoutingStrategy, DispatchMessageCompensatingStrategy>();
            
            builder.Services.Scan(s =>
                        s.FromAssemblies(AppDomain.CurrentDomain.GetAssemblies())
                            .AddClasses(c => c.AssignableTo(typeof(IMessagingInfrastructureReceiver<>)))
                            .UsingRegistrationStrategy(RegistrationStrategy.Skip)
                            .AsImplementedInterfaces()
                            .WithSingletonLifetime());

            return builder;
        }
    }
}
