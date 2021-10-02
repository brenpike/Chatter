using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.CQRS.Pipeline;
using Chatter.CQRS.Queries;
using Microsoft.Extensions.Configuration;
using Scrutor;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class CqrsExtensions
    {
        static CommandPipelineBuilder CreatePipelineBuilder(this IServiceCollection services)
            => new CommandPipelineBuilder(services);

        /// <summary>
        /// Adds chatter cqrs capabilities
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> used to register services used for cqrs capabilities</param>
        /// <param name="configuration">The <see cref="IConfiguration"/> used for configuration based settings</param>
        /// <param name="pipelineBuilder">An optional builder used to define an <see cref="ICommandBehaviorPipeline{TMessage}"/></param>
        /// <param name="messageHandlerSourceBuilder">An optional builder used to define a <see cref="AssemblySourceFilter"/>. Assemblies will be used to find <see cref="IMessageHandler{TMessage}"/> for registration.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services, IConfiguration configuration, Action<CommandPipelineBuilder> pipelineBuilder = null, Action<AssemblySourceFilterBuilder> messageHandlerSourceBuilder = null)
        {
            var filterBuilder = AssemblySourceFilterBuilder.New();
            messageHandlerSourceBuilder?.Invoke(filterBuilder);
            var filter = filterBuilder.Build();
            var chatterBuilder = ChatterBuilder.Create(services, configuration, filter);

            var assemblies = filter.Apply();

            chatterBuilder.Services.AddMessageHandlers(assemblies);
            chatterBuilder.Services.AddQueryHandlers(assemblies);

            chatterBuilder.Services.AddScoped<IMessageDispatcherProvider, MessageDispatcherProvider>();

            chatterBuilder.Services.AddInMemoryMessageDispatchers();
            chatterBuilder.Services.AddInMemoryQueryDispatcher();

            chatterBuilder.Services.AddIfNotRegistered<IExternalDispatcher, NoOpExternalDispatcher>(ServiceLifetime.Scoped);

            return AddCommandPipeline(chatterBuilder, pipelineBuilder);
        }

        /// <summary>
        /// Adds chatter cqrs capabilities
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> used to register services used for cqrs capabilities</param>
        /// <param name="configuration">The <see cref="IConfiguration"/> used for configuration based settings</param>
        /// <param name="pipelineBuilder">An optional builder used to define an <see cref="ICommandBehaviorPipeline{TMessage}"/></param>
        /// <param name="markerTypesForRequiredAssemblies">Marker types whose parent assemblies will be used to find <see cref="IMessageHandler{TMessage}"/> for registration.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services, IConfiguration configuration, Action<CommandPipelineBuilder> pipelineBuilder = null, params Type[] markerTypesForRequiredAssemblies)
            => services.AddChatterCqrs(configuration, pipelineBuilder, b => b.WithMarkerTypes(markerTypesForRequiredAssemblies));

        /// <summary>
        /// Adds chatter cqrs capabilities
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> used to register services used for cqrs capabilities</param>
        /// <param name="configuration">The <see cref="IConfiguration"/> used for configuration based settings</param>
        /// <param name="markerTypesForRequiredAssemblies">Marker types whose parent assemblies will be used to find <see cref="IMessageHandler{TMessage}"/> for registration.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services, IConfiguration configuration, params Type[] markerTypesForRequiredAssemblies)
            => services.AddChatterCqrs(configuration, null, b => b.WithMarkerTypes(markerTypesForRequiredAssemblies));

        /// <summary>
        /// Adds chatter cqrs capabilities
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> used to register services used for cqrs capabilities</param>
        /// <param name="configuration">The <see cref="IConfiguration"/> used for configuration based settings</param>
        /// <param name="handlerAssemblies">Assemblies will be used to find <see cref="IMessageHandler{TMessage}"/> for registration.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services, IConfiguration configuration, params Assembly[] handlerAssemblies)
            => services.AddChatterCqrs(configuration, null, b => b.WithExplicitAssemblies(handlerAssemblies));

        /// <summary>
        /// Adds chatter cqrs capabilities
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> used to register services used for cqrs capabilities</param>
        /// <param name="configuration">The <see cref="IConfiguration"/> used for configuration based settings</param>
        /// <param name="handlerNamespaceSelector">A namespace selector used to find assemblies containing types with matching namespaces or assemblies with matching FullName. Supports '*' and '?' wildcard values. Matching assemblies used to find <see cref="IMessageHandler{TMessage}"/> for registration.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services, IConfiguration configuration, string handlerNamespaceSelector)
            => services.AddChatterCqrs(configuration, null, b => b.WithNamespaceSelector(handlerNamespaceSelector));

        internal static IChatterBuilder AddCommandPipeline(this IChatterBuilder chatterBuilder, Action<CommandPipelineBuilder> pipelineBuilder)
        {
            var pipeline = chatterBuilder.Services.CreatePipelineBuilder();

            if (pipeline is null)
            {
                return chatterBuilder;
            }

            chatterBuilder.Services.AddTransient(typeof(ICommandBehaviorPipeline<>), typeof(CommandBehaviorPipeline<>));

            pipelineBuilder?.Invoke(pipeline);

            return chatterBuilder;
        }

        internal static IServiceCollection AddMessageHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
        {
            AddCommandHandlers(services, assemblies);
            AddEventHandlers(services, assemblies);
            return services;
        }

        internal static IServiceCollection AddEventHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
        {
            services.Scan(s =>
               s.FromAssemblies(assemblies)
                   .AddClasses(c => c.AssignableTo(typeof(IMessageHandler<>))
                        .Where(handler => IsValidMessageHandler(handler, typeof(IEvent))))
                   .UsingRegistrationStrategy(RegistrationStrategy.Append)
                   .AsImplementedInterfaces()
                   .WithTransientLifetime());
            return services;
        }

        internal static IServiceCollection AddCommandHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
        {
            services.Scan(s =>
               s.FromAssemblies(assemblies)
                   .AddClasses(c => c.AssignableTo(typeof(IMessageHandler<>))
                        .Where(handler => IsValidMessageHandler(handler, typeof(ICommand))))
                   .UsingRegistrationStrategy(RegistrationStrategy.Replace())
                   .AsImplementedInterfaces()
                   .WithTransientLifetime());
            return services;
        }

        internal static bool IsValidMessageHandler(this Type type, Type genericParameterMatchType)
            => (!type.IsGenericType || type.IsGenericTypeWithNonGenericTypeParameters())
                && type.IsImplementingOpenGenericTypeWithMatchingTypeParameter(typeof(IMessageHandler<>), genericParameterMatchType);

        internal static IServiceCollection AddQueryHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
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
    }
}
