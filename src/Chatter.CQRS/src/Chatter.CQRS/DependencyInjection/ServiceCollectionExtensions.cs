using Chatter.CQRS.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scrutor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddPipelineBehavior(this IServiceCollection services, Type behaviorType)
        {
            _ = behaviorType ?? throw new ArgumentNullException(nameof(behaviorType), "Cannot add null behavior type to command pipeline.");
            var ii = behaviorType.GetTypeInfo()?.ImplementedInterfaces ?? throw new NullReferenceException($"Unable to get implemented interfaces for '{behaviorType.Name}'.");

            if (!ii.Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandBehavior<>)))
            {
                throw new ArgumentException($"The supplied type must implement {typeof(ICommandBehavior<>).Name}", nameof(behaviorType));
            }

            if (behaviorType.IsGenericTypeDefinition)
            {
                services.RegisterBehaviorForAllCommands(behaviorType);
            }
            else
            {
                services.RegisterBehaviorForCommand(behaviorType);
            }

            return services;
        }

        public static IEnumerable<ServiceDescriptor> GetServiceDescriptorsByImplementationType(this IServiceCollection services, Type implementationType)
        {
            return services.Where(sd =>
                            sd.ImplementationType != null &&
                            ((sd.ImplementationType.IsGenericTypeDefinition && sd.ImplementationType.GetGenericTypeDefinition() == implementationType) ||
                            (!sd.ImplementationType.IsGenericTypeDefinition && sd.ImplementationType == implementationType)));
        }

        /// <summary>
        /// Move all <see cref="ServiceDescriptor"/> whose <see cref="ServiceDescriptor.ImplementationType"/> matches type <paramref name="typeOfServiceToMove"/> before <see cref="ServiceDescriptor"/> with <see cref="ServiceDescriptor.ImplementationType"/> of type <paramref name="typeOfServiceToInsertBefore"/> in the <see cref="ServiceCollection"/>
        /// </summary>
        /// <param name="services">The <see cref="ServiceCollection"/> in which types will be moved</param>
        /// <param name="typeOfServiceToMove">The type of the <see cref="ServiceDescriptor"/> to be moved within the <see cref="ServiceCollection"/></param>
        /// <param name="typeOfServiceToInsertBefore">The type of <see cref="ServiceDescriptor"/> that <paramref name="typeOfServiceToMove"/> should be inserted before in the <see cref="ServiceCollection"/></param>
        /// <returns><see cref="IServiceCollection"/></returns>
        public static IServiceCollection MoveServiceDescriptorBefore(this IServiceCollection services, Type typeOfServiceToMove, Type typeOfServiceToInsertBefore)
        {
            _ = typeOfServiceToMove ?? throw new ArgumentNullException(nameof(typeOfServiceToMove), $"The type of {nameof(ServiceDescriptor)} to move cannot be null");
            _ = typeOfServiceToInsertBefore ?? throw new ArgumentNullException(nameof(typeOfServiceToInsertBefore), $"The type of {nameof(ServiceDescriptor)} to move services of type {nameof(typeOfServiceToMove)} before cannot be null");

            var serviceToInsertBefore = services.GetServiceDescriptorsByImplementationType(typeOfServiceToInsertBefore).FirstOrDefault();

            if (serviceToInsertBefore == null)
            {
                return services;
            }

            var indexOfServiceToInsertBefore = services.IndexOf(serviceToInsertBefore);
            var serviceDescriptorsToMove = services.GetServiceDescriptorsByImplementationType(typeOfServiceToMove);

            if (serviceDescriptorsToMove.All(sd => services.IndexOf(sd) < indexOfServiceToInsertBefore))
            {
                return services;
            }

            for (int i = 0; i < serviceDescriptorsToMove.Count(); i++)
            {
                var move = serviceDescriptorsToMove.ElementAt(i);
                if (services.IndexOf(move) > indexOfServiceToInsertBefore)
                {
                    services.Remove(move);
                    services.Insert(indexOfServiceToInsertBefore, move);
                    indexOfServiceToInsertBefore = services.IndexOf(serviceToInsertBefore);
                }
            }

            return services;
        }

        public static IServiceCollection RegisterBehaviorForAllCommands(this IServiceCollection services, Type openGenericBehaviorType)
        {
            _ = openGenericBehaviorType ?? throw new ArgumentNullException(nameof(openGenericBehaviorType), "A non-null command behavior type is required");

            if (!openGenericBehaviorType.IsGenericTypeDefinition)
            {
                throw new ArgumentException($"An open generic behavior type is required, but a closed generic was supplied: '{openGenericBehaviorType.Name}'", nameof(openGenericBehaviorType));
            }

            if (!openGenericBehaviorType.GetInterfaces().Any(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ICommandBehavior<>)))
            {
                throw new ArgumentException($"Generic type definition must be {typeof(ICommandBehavior<>).Name}", nameof(openGenericBehaviorType));
            }

            services.Scan(s =>
                   s.FromAssemblies(openGenericBehaviorType.Assembly)
                       .AddClasses(c => c.AssignableTo(openGenericBehaviorType))
                       .UsingRegistrationStrategy(RegistrationStrategy.Replace(ReplacementBehavior.ImplementationType))
                       .AsImplementedInterfaces()
                       .WithTransientLifetime());

            return services;
        }

        public static IServiceCollection RegisterBehaviorForCommand(this IServiceCollection services, Type closedGenericBehaviorType)
        {
            var commandBehaviorType = typeof(ICommandBehavior<>);

            _ = closedGenericBehaviorType ?? throw new ArgumentNullException(nameof(closedGenericBehaviorType), "A non-null command behavior type is required");

            if (closedGenericBehaviorType.IsGenericTypeDefinition)
            {
                throw new ArgumentException($"A closed generic behavior type is required, but an open generic was supplied: '{closedGenericBehaviorType.Name}'", nameof(closedGenericBehaviorType));
            }

            if (!closedGenericBehaviorType.GetInterfaces().Any(t => t.IsGenericType && t.GetGenericTypeDefinition() == commandBehaviorType))
            {
                throw new ArgumentException($"Generic type definition must be {commandBehaviorType.Name}", nameof(closedGenericBehaviorType));
            }

            var behaviorCommandType = closedGenericBehaviorType.GetGenericArguments().Single();

            var closedCommandBehaviorInterface = commandBehaviorType.MakeGenericType(behaviorCommandType);
            services.AddTransient(closedCommandBehaviorInterface, closedGenericBehaviorType);

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

        public static IServiceCollection Replace<TService>(this IServiceCollection services,
                                                           ServiceLifetime lifetime,
                                                           Func<IServiceProvider, TService> factory)
            where TService : class
        {
            services.RemoveAll(typeof(TService));

            var descriptorToAdd = new ServiceDescriptor(typeof(TService), factory, lifetime);

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

        public static IServiceCollection AddIfNotRegistered<TService>(this IServiceCollection services, ServiceLifetime lifetime, Func<IServiceProvider, TService> factory)
            where TService : class
        {
            if (services.Any(s => s.ServiceType == typeof(TService)))
            {
                return services;
            }

            var descriptorToAdd = new ServiceDescriptor(typeof(TService), factory, lifetime);
            services.Add(descriptorToAdd);

            return services;
        }

        public static IServiceCollection AddIfNotRegistered<TService>(this IServiceCollection services, ServiceLifetime lifetime)
            where TService : class
            => services.AddIfNotRegistered<TService, TService>(lifetime);
    }
}
