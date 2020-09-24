using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.Saga;
using System;
using System.Linq;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class SagaExtensions
    {
        public static IChatterBuilder AddSagas(this IChatterBuilder builder)
        {
            builder.Services.AddScoped<IDispatchMessages, SagaMessageDispatcher>();
            builder.Services.AddSingleton<ISagaPersister, InMemorySagaPersister>();
            builder.Services.AddScoped<ISagaOrchestrator, SagaOrchestrator>();
            builder.Services.AddScoped<ISagaInitializer, SagaInitializer>();
            builder.Services.AddScoped<ISagaOptionsProvider, SagaOptionsProvider>();

            return builder;
        }

        private static bool IsGenericInterface(Type type)
        {
            if (!type.IsInterface)
            {
                return false;
            }

            if (!type.IsGenericType)
            {
                return false;
            }

            return true;
        }
    }
}
