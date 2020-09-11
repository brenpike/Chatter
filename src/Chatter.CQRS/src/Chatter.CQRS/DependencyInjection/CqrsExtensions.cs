using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.CQRS.Pipeline;
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
            IEnumerable<Assembly> assemblies = GetAssembliesFromMarkerTypes(markerTypesForRequiredAssemblies);

            var builder = ChatterBuilder.Create(services);

            builder.Services.AddMessageHandlers(assemblies);
            builder.Services.AddSingleton<IMessageDispatcherProvider, MessageDispatcherProvider>();
            builder.Services.AddInMemoryMessageDispatchers();
            builder.Services.AddInMemoryQueryDispatcher();
            builder.Services.AddQueryHandlers(assemblies);
            return builder;
        }

        public static PipelineBuilder CreatePipelineBuiler(this IServiceCollection services)
        {
            return new PipelineBuilder(services);
        }

        public static IChatterBuilder AddCommandPipeline(this IChatterBuilder chatterBuilder, Action<PipelineBuilder> pipelineBulder)
        {
            var pipeline = chatterBuilder.Services.CreatePipelineBuiler();

            if (pipeline is null)
            {
                return chatterBuilder;
            }

            pipelineBulder?.Invoke(pipeline);

            return chatterBuilder;
        }

        public static IServiceCollection AddMessageHandlers(this IServiceCollection services, params Type[] markerTypesForRequiredAssemblies)
        {
            IEnumerable<Assembly> assemblies = markerTypesForRequiredAssemblies.GetAssembliesFromMarkerTypes();
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
            IEnumerable<Assembly> assemblies = GetAssembliesFromMarkerTypes(markerTypesForRequiredAssemblies);
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

            services.AddSingleton<ICommandBehaviorPipeline, CommandBehaviorPipeline>();

            services.AddSingleton<IDispatchMessages, CommandDispatcher>();
            services.AddSingleton<IDispatchMessages, EventDispatcher>();
            return services;
        }

        public static IServiceCollection AddInMemoryQueryDispatcher(this IServiceCollection services)
        {
            services.AddSingleton<IQueryDispatcher, QueryDispatcher>();
            return services;
        }

        public static IEnumerable<Assembly> GetAssembliesFromMarkerTypes(this Type[] markerTypesForRequiredAssemblies)
        {
            var assemblies = markerTypesForRequiredAssemblies.Select(t => t.GetTypeInfo().Assembly).ToList();
            return assemblies.Union(AppDomain.CurrentDomain.GetAssemblies());
        }
    }
}
