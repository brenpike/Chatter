using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Events;
using Chatter.CQRS.Queries;
using Scrutor;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class CqrsExtensions
    {
        public static IChatterBuilder AddChatterCqrs(this IServiceCollection services)
        {
            var builder = ChatterBuilder.Create(services);

            builder.Services.AddMessageHandlers();
            builder.Services.AddSingleton<IMessageDispatcherFactory, MessageDispatcherFactory>();
            builder.Services.AddInMemoryMessageDispatchers();
            builder.Services.AddInMemoryQueryDispatcher();
            builder.Services.AddQueryHandlers();
            return builder;
        }

        public static IServiceCollection AddMessageHandlers(this IServiceCollection services)
        {
            services.Scan(s =>
                   s.FromAssemblies(AppDomain.CurrentDomain.GetAssemblies())
                       .AddClasses(c => c.AssignableTo(typeof(IMessageHandler<>)))
                       .UsingRegistrationStrategy(RegistrationStrategy.Append)
                       .AsImplementedInterfaces()
                       .WithTransientLifetime());
            return services;
        }
      
        public static IServiceCollection AddQueryHandlers(this IServiceCollection services)
        {
            services.Scan(s =>
                   s.FromAssemblies(AppDomain.CurrentDomain.GetAssemblies())
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
    }
}
