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
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class CqrsExtensions
    {
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services, IConfiguration configuration, params Type[] markerTypesForRequiredAssemblies)
        {
            IEnumerable<Assembly> assemblies = GetAssembliesFromMarkerTypes(markerTypesForRequiredAssemblies);

            var builder = ChatterBuilder.Create(services, configuration, assemblies);

            builder.Services.AddMessageHandlers(assemblies);
            builder.Services.AddQueryHandlers(assemblies);

            builder.Services.AddScoped<IMessageDispatcherProvider, MessageDispatcherProvider>();

            builder.Services.AddInMemoryMessageDispatchers();
            builder.Services.AddInMemoryQueryDispatcher();

            return builder;
        }

        static PipelineBuilder CreatePipelineBuilder(this IServiceCollection services) 
            => new PipelineBuilder(services);

        public static IChatterBuilder AddCommandPipeline(this IChatterBuilder chatterBuilder, Action<PipelineBuilder> pipelineBulder)
        {
            var pipeline = chatterBuilder.Services.CreatePipelineBuilder();

            if (pipeline is null)
            {
                return chatterBuilder;
            }

            chatterBuilder.Services.Scan(s =>
                                s.FromAssemblies(chatterBuilder.MarkerAssemblies)
                                .AddClasses(c => c.AssignableTo(typeof(ICommandBehaviorPipeline<>)))
                                .UsingRegistrationStrategy(RegistrationStrategy.Throw)
                                .AsImplementedInterfaces()
                                .WithTransientLifetime());

            pipelineBulder?.Invoke(pipeline);

            return chatterBuilder;
        }

        static IServiceCollection AddMessageHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
        {
            AddCommandHandlers(services, assemblies);
            AddEventHandlers(services, assemblies);
            return services;
        }

        static IServiceCollection AddEventHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
        {
            services.Scan(s =>
               s.FromAssemblies(assemblies)
                   .AddClasses(c => c.AssignableTo(typeof(IMessageHandler<>))
                        .Where(handler => FilterMessageHandlerByType(handler, typeof(IEvent))))
                   .UsingRegistrationStrategy(RegistrationStrategy.Append)
                   .AsImplementedInterfaces()
                   .WithTransientLifetime());
            return services;
        }

        static IServiceCollection AddCommandHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
        {
            services.Scan(s =>
               s.FromAssemblies(assemblies)
                   .AddClasses(c => c.AssignableTo(typeof(IMessageHandler<>))
                        .Where(handler => FilterMessageHandlerByType(handler, typeof(ICommand))))
                   .UsingRegistrationStrategy(RegistrationStrategy.Throw)
                   .AsImplementedInterfaces()
                   .WithTransientLifetime());
            return services;
        }

        static bool FilterMessageHandlerByType(Type handler, Type filterType)
        {
            return handler.GetTypeInfo().ImplementedInterfaces
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>))
                    .Any(mhi => mhi.GetGenericArguments()
                        .SingleOrDefault().GetTypeInfo().ImplementedInterfaces
                            .Any(t => t == filterType));
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
            services.AddScoped<IMessageDispatcher, MessageDispatcher>();
            services.AddScoped<IDispatchMessages, CommandDispatcher>();
            services.AddScoped<IDispatchMessages, EventDispatcher>();
            return services;
        }

        public static IServiceCollection AddInMemoryQueryDispatcher(this IServiceCollection services)
        {
            services.AddScoped<IQueryDispatcher, QueryDispatcher>();
            return services;
        }

        public static IEnumerable<Assembly> GetAssembliesFromMarkerTypes(this Type[] markerTypesForRequiredAssemblies)
        {
            var assemblies = markerTypesForRequiredAssemblies.Select(t => t.GetTypeInfo().Assembly).ToList();
            return assemblies.Union(AppDomain.CurrentDomain.GetAssemblies());
        }
    }
}
