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
            builder.Services.AddSingleton<IDispatchMessages, SagaMessageDispatcher>();
            builder.Services.AddSingleton<ISagaPersister, InMemorySagaPersister>();
            builder.Services.AddSingleton<ISagaOrchestrator, SagaOrchestrator>();
            builder.Services.AddSingleton<ISagaInitializer, SagaInitializer>();
            builder.Services.AddSingleton<ISagaOptionsProvider, SagaOptionsProvider>();

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

        private static bool DoesTypeImplementOpenGenericInterface(Type typeToExecuteSearch, Type typeToFind)
        {
            if (!IsGenericInterface(typeToFind))
            {
                throw new ArgumentException($"The supplied type must be an interface.", nameof(typeToFind));
            }

            return typeToExecuteSearch.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeToFind);
        }
    }
}
