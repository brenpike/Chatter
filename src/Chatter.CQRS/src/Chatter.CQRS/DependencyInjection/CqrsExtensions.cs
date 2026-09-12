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
using System.Linq;
using System.Reflection;
using System.Text;

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
        /// <param name="messageHandlerSourceBuilder">An optional builder used to define a <see cref="AssemblySourceFilter"/>. When explicit assemblies or marker types are supplied to the builder and no namespace selector is also configured, only those assemblies are scanned to find <see cref="IMessageHandler{TMessage}"/> for registration; configuring <see cref="AssemblySourceFilterBuilder.WithNamespaceSelector(string)"/> widens the scan to include matching loaded assemblies as well.</param>
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
        /// <param name="markerTypesForRequiredAssemblies">Marker types whose parent assemblies are the only assemblies scanned to find <see cref="IMessageHandler{TMessage}"/> for registration, unless a namespace selector is also configured via <see cref="AssemblySourceFilterBuilder.WithNamespaceSelector(string)"/>, which widens the scan to include matching loaded assemblies as well.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services, IConfiguration configuration, Action<CommandPipelineBuilder> pipelineBuilder = null, params Type[] markerTypesForRequiredAssemblies)
            => services.AddChatterCqrs(configuration, pipelineBuilder, b => b.WithMarkerTypes(markerTypesForRequiredAssemblies));

        /// <summary>
        /// Adds chatter cqrs capabilities
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> used to register services used for cqrs capabilities</param>
        /// <param name="configuration">The <see cref="IConfiguration"/> used for configuration based settings</param>
        /// <param name="markerTypesForRequiredAssemblies">Marker types whose parent assemblies are the only assemblies scanned to find <see cref="IMessageHandler{TMessage}"/> for registration, unless a namespace selector is also configured via <see cref="AssemblySourceFilterBuilder.WithNamespaceSelector(string)"/>, which widens the scan to include matching loaded assemblies as well.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services, IConfiguration configuration, params Type[] markerTypesForRequiredAssemblies)
            => services.AddChatterCqrs(configuration, null, b => b.WithMarkerTypes(markerTypesForRequiredAssemblies));

        /// <summary>
        /// Adds chatter cqrs capabilities
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> used to register services used for cqrs capabilities</param>
        /// <param name="configuration">The <see cref="IConfiguration"/> used for configuration based settings</param>
        /// <param name="handlerAssemblies">The only assemblies scanned to find <see cref="IMessageHandler{TMessage}"/> for registration, unless a namespace selector is also configured via <see cref="AssemblySourceFilterBuilder.WithNamespaceSelector(string)"/>, which widens the scan to include matching loaded assemblies as well.</param>
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

        /// <summary>
        /// Fails composition when more than one handler is found for the same command.
        /// </summary>
        /// <param name="chatterBuilder">The <see cref="IChatterBuilder"/> whose <see cref="IAssemblySourceFilter"/> defines the assemblies that are checked</param>
        /// <returns>The same <see cref="IChatterBuilder"/> instance</returns>
        /// <exception cref="InvalidOperationException">Thrown when a command is handled by more than one scanned handler</exception>
        public static IChatterBuilder ThrowOnDuplicateCommandHandlers(this IChatterBuilder chatterBuilder)
        {
            var ambiguousCommands = FindCommandsWithCompetingHandlers(chatterBuilder.AssemblySourceFilter.Apply());

            if (ambiguousCommands.Count > 0)
            {
                throw new InvalidOperationException(DescribeAmbiguousCommands(ambiguousCommands));
            }

            return chatterBuilder;
        }

        /// <summary>
        /// Finds every command handled by more than one scanned handler by probing the command handler scan into a
        /// throwaway <see cref="IServiceCollection"/> with an appending strategy, so that every competing handler
        /// survives instead of displacing the one before it. INVARIANT: the probe must derive its candidates from the
        /// same scan <see cref="AddCommandHandlers"/> uses, never from a re-derived type filter, otherwise the check
        /// reports handlers that are never registered.
        /// </summary>
        private static IReadOnlyList<KeyValuePair<Type, IReadOnlyList<Type>>> FindCommandsWithCompetingHandlers(IEnumerable<Assembly> assemblies)
            => ScanCommandHandlers(new ServiceCollection(), assemblies, RegistrationStrategy.Append)
                .Where(descriptor => descriptor.ImplementationType is not null)
                .GroupBy(descriptor => descriptor.ServiceType, descriptor => descriptor.ImplementationType)
                .Select(handlersForCommand => new KeyValuePair<Type, IReadOnlyList<Type>>(
                    handlersForCommand.Key.GetGenericArguments()[0],
                    handlersForCommand.Distinct().OrderBy(handler => handler.FullName, StringComparer.Ordinal).ToList()))
                .Where(ambiguousCommand => ambiguousCommand.Value.Count >= 2)
                .OrderBy(ambiguousCommand => ambiguousCommand.Key.FullName, StringComparer.Ordinal)
                .ToList();

        private static string DescribeAmbiguousCommands(IReadOnlyList<KeyValuePair<Type, IReadOnlyList<Type>>> ambiguousCommands)
        {
            var description = new StringBuilder("More than one command handler was found for the same command. Command handlers are registered using a replace strategy, so only the last handler scanned is registered, and the scan order is derived from assembly load order and the order in which an assembly defines its types, neither of which is specified.");

            foreach (var ambiguousCommand in ambiguousCommands)
            {
                description.Append(Environment.NewLine)
                           .Append(ambiguousCommand.Key.FullName)
                           .Append(" is handled by ")
                           .Append(string.Join(", ", ambiguousCommand.Value.Select(handler => handler.FullName)));
            }

            return description.ToString();
        }

        internal static IChatterBuilder AddCommandPipeline(this IChatterBuilder chatterBuilder, Action<CommandPipelineBuilder> pipelineBuilder)
        {
            var pipeline = chatterBuilder.Services.CreatePipelineBuilder();

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
                   .As(handler => handler.GetMessageHandlerInterfacesFor(typeof(IEvent)))
                   .WithTransientLifetime());
            return services;
        }

        internal static IServiceCollection AddCommandHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
            => ScanCommandHandlers(services, assemblies, RegistrationStrategy.Replace());

        /// <summary>
        /// The sole command handler scan. Every command handler candidate set in this assembly comes from here, so
        /// that the registration and the <see cref="ThrowOnDuplicateCommandHandlers(IChatterBuilder)"/> check always
        /// select the same types and differ only by <paramref name="strategy"/>.
        /// </summary>
        private static IServiceCollection ScanCommandHandlers(IServiceCollection services, IEnumerable<Assembly> assemblies, RegistrationStrategy strategy)
        {
            services.Scan(s =>
               s.FromAssemblies(assemblies)
                   .AddClasses(c => c.AssignableTo(typeof(IMessageHandler<>))
                        .Where(handler => IsValidMessageHandler(handler, typeof(ICommand))))
                   .UsingRegistrationStrategy(strategy)
                   .As(handler => handler.GetMessageHandlerInterfacesFor(typeof(ICommand)))
                   .WithTransientLifetime());
            return services;
        }

        internal static IEnumerable<Type> GetMessageHandlerInterfacesFor(this Type type, Type genericParameterMatchType)
            => type.GetImplementedInterfacesThatMatchOpenGenericType(typeof(IMessageHandler<>))
                .Where(handlerInterface => handlerInterface.GetImplementedInterfacesOfSingleGenericTypeArgument()
                    .Any(implementedByMessage => implementedByMessage == genericParameterMatchType));

        internal static bool IsClosedHandlerType(this Type type)
            => !type.IsGenericType || type.IsGenericTypeWithNonGenericTypeParameters();

        internal static bool IsValidMessageHandler(this Type type, Type genericParameterMatchType)
            => type.IsClosedHandlerType()
                && type.IsImplementingOpenGenericTypeWithMatchingTypeParameter(typeof(IMessageHandler<>), genericParameterMatchType);

        internal static IServiceCollection AddQueryHandlers(this IServiceCollection services, IEnumerable<Assembly> assemblies)
        {
            services.Scan(s =>
                   s.FromAssemblies(assemblies)
                       .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>))
                            .Where(handler => handler.IsClosedHandlerType()))
                       .UsingRegistrationStrategy(RegistrationStrategy.Throw)
                       .As(handler => handler.GetImplementedInterfacesThatMatchOpenGenericType(typeof(IQueryHandler<,>)))
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
