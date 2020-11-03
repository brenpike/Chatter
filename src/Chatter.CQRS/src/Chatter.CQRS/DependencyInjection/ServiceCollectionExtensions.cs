using Chatter.CQRS.Commands;
using Chatter.CQRS.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scrutor;
using System;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection InsertServiceBefore(this IServiceCollection services, Type serviceToInsert, Type serviceToInsertBefore)
        {
            var serviceToPrepend = services.Where(sd =>
                            sd.ImplementationType != null &&
                            ((sd.ImplementationType.IsGenericTypeDefinition && sd.ImplementationType.GetGenericTypeDefinition() == serviceToInsertBefore) ||
                            (!sd.ImplementationType.IsGenericTypeDefinition && sd.ImplementationType == serviceToInsertBefore))).FirstOrDefault();

            if (serviceToPrepend == null)
            {
                return services;
            }

            var indexOfServiceToInsertBefore = services.IndexOf(serviceToPrepend);

            var behaviorsToMove = services.Where(sd =>
                            sd.ImplementationType != null &&
                            ((sd.ImplementationType.IsGenericTypeDefinition && sd.ImplementationType.GetGenericTypeDefinition() == serviceToInsert) ||
                            (!sd.ImplementationType.IsGenericTypeDefinition && sd.ImplementationType == serviceToInsert))).ToList();


            var indexOfServiceToInsert = services.IndexOf(behaviorsToMove.FirstOrDefault());
            if (indexOfServiceToInsert == -1 || indexOfServiceToInsert < indexOfServiceToInsertBefore)
            {
                return services;
            }

            foreach (var item in behaviorsToMove)
            {
                services.Remove(item);
                services.Insert(indexOfServiceToInsertBefore, item);
                indexOfServiceToInsertBefore++;
            }

            return services;
        }

        public static IServiceCollection RegisterBehaviorForAllCommands(this IServiceCollection services, Type openGenericBehaviorType)
        {
            if (!openGenericBehaviorType.IsGenericTypeDefinition)
            {
                throw new ArgumentException($"An open generic behavior type is required, but a closed generic was supplied: '{openGenericBehaviorType.Name}'", nameof(openGenericBehaviorType));
            }

            services.Scan(s =>
                   s.FromAssemblies(openGenericBehaviorType.GetTypeInfo().Assembly)
                       .AddClasses(c => c.AssignableTo(openGenericBehaviorType))
                       .UsingRegistrationStrategy(RegistrationStrategy.Replace(ReplacementBehavior.ImplementationType))
                       .AsImplementedInterfaces()
                       .WithTransientLifetime());

            return services;
        }

        public static IServiceCollection RegisterBehaviorForCommand(this IServiceCollection services, Type closedGenericBehaviorType)
        {
            if (closedGenericBehaviorType.IsGenericTypeDefinition)
            {
                throw new ArgumentException($"A closed generic behavior type is required, but an open generic was supplied: '{closedGenericBehaviorType.Name}'", nameof(closedGenericBehaviorType));
            }

            var behaviorCommandType = closedGenericBehaviorType.GetGenericArguments().SingleOrDefault();

            if (!(behaviorCommandType is ICommand))
            {
                throw new ArgumentException($"Generic type argument for '{closedGenericBehaviorType.Name}' is not of type '{typeof(ICommand).Name}'", nameof(closedGenericBehaviorType));
            }

            var closedCommandBehaviorInterface = typeof(ICommandBehavior<>).MakeGenericType(behaviorCommandType);
            services.Replace(ServiceDescriptor.Transient(closedCommandBehaviorInterface, closedGenericBehaviorType));

            return services;
        }

        public static IServiceCollection Replace<TService, TImplementation>(this IServiceCollection services, ServiceLifetime lifetime)
            where TService : class
            where TImplementation : class, TService
        {
            services.RemoveAll(typeof(TService));

            var descriptorToAdd = new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime);

            services.Add(descriptorToAdd);

            return services;
        }

        public static IServiceCollection AddIfNotRegistered<TService, TImplementation>(this IServiceCollection services, ServiceLifetime lifetime)
            where TService : class
            where TImplementation : class, TService
        {
            if (services.Any(s => s.ServiceType == typeof(TService)))
            {
                return services;
            }

            var descriptorToAdd = new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime);
            services.Add(descriptorToAdd);

            return services;
        }
    }
}
