using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.CQRS.Queries;
using Scrutor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class CqrsExtensions
    {
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services, params Type[] markerTypesForRequiredAssemblies)
        {
            IEnumerable<Assembly> assemblies = GetAssemblies(markerTypesForRequiredAssemblies);

            var builder = ChatterBuilder.Create(services);

            builder.Services.AddMessageHandlers(assemblies);
            builder.Services.AddSingleton<IMessageDispatcherFactory, MessageDispatcherFactory>();
            builder.Services.AddInMemoryMessageDispatchers();
            builder.Services.AddInMemoryQueryDispatcher();
            builder.Services.AddQueryHandlers(assemblies);
            return builder;
        }

        public static IServiceCollection AddMessageHandlers(this IServiceCollection services, params Type[] markerTypesForRequiredAssemblies)
        {
            IEnumerable<Assembly> assemblies = GetAssemblies(markerTypesForRequiredAssemblies);
            return AddMessageHandlers(services, assemblies);
        }

        static IServiceCollection AddMessageHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
        {
            services.Scan(s =>
               s.FromAssemblies(assemblies)
                   .AddClasses(c => c.AssignableTo(typeof(IMessageHandler<>)))
                   .UsingRegistrationStrategy(RegistrationStrategy.Append)
                   .AsImplementedInterfaces()
                   .WithTransientLifetime());
            return services;
        }

        public static IServiceCollection AddQueryHandlers(this IServiceCollection services, params Type[] markerTypesForRequiredAssemblies)
        {
            IEnumerable<Assembly> assemblies = GetAssemblies(markerTypesForRequiredAssemblies);
            return AddQueryHandlers(services, assemblies);
        }

        static IServiceCollection AddQueryHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
        {
            services.Scan(s =>
                   s.FromAssemblies(assemblies)
                       .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)))
                       .UsingRegistrationStrategy(RegistrationStrategy.Throw)
                       .AsImplementedInterfaces()
                       .WithTransientLifetime());
            return services;
        }

        public static IServiceCollection AddInMemoryMessageDispatchers(this IServiceCollection services)
        {
            services.AddSingleton<IMessageDispatcher, MessageDispatcher>();
            services.AddSingleton<IMessageDispatcherProvider, CommandDispatcherProvider>();
            services.AddSingleton<IMessageDispatcherProvider, EventDispatcherProvider>();
            return services;
        }

        public static IServiceCollection AddInMemoryQueryDispatcher(this IServiceCollection services)
        {
            services.AddSingleton<IQueryDispatcher, QueryDispatcher>();
            return services;
        }
        private static IEnumerable<Assembly> GetAssemblies(Type[] markerTypesForRequiredAssemblies)
        {
            var assemblies = markerTypesForRequiredAssemblies.Select(t => t.GetTypeInfo().Assembly);
            if (assemblies is null || assemblies?.Count() == 0)
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }

            return assemblies;
        }
    }
}
