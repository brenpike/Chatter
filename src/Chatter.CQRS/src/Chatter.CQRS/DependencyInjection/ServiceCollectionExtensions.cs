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
            var ii = behaviorType.GetTypeInfo().ImplementedInterfaces ?? throw new NullReferenceException($"Unable to get implemented interfaces for '{behaviorType.Name}'.");

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
                       // INVARIANT: this scan and the three handler scans in CqrsExtensions register non-public
                       // classes, because consumer handlers and behaviors are often internal and Scrutor >= 6.0.1
                       // defaults AddClasses to publicOnly: true, which would silently drop them.
                       // Oracle: MustRegisterANonPublicOpenGenericBehavior, MustRegisterAndReportNonPublicCompetingHandlers
                       // and, in the Azure Service Bus tests, MustRegisterTheInternalTransactionScopeSupressionBehavior;
                       // the event and query scans are pinned by WhenAddingEventHandlers.MustRegisterEventHandler and
                       // WhenAddingQueryHandlers.MustRegisterAllQueryHandlers, whose fixture handlers are private.
                       // Mutation that reddens them: dropping the publicOnly: false argument (or setting it true) at
                       // all four sites (observed under Scrutor 3.3.0 with publicOnly: true, and under 7.0.0 by
                       // dropping the argument).
                       .AddClasses(c => c.AssignableTo(openGenericBehaviorType), publicOnly: false)
                       .UsingRegistrationStrategy(RegistrationStrategy.Replace(ReplacementBehavior.ImplementationType))
                       // INVARIANT: for a scanned class that declares ICommandBehavior<> at a single closing, this
                       // selector emits at most one service type, so Replace(ReplacementBehavior.ImplementationType)
                       // cannot delete a descriptor this same scan just added for that class. A collateral interface
                       // on the class, generic or not, is not a service type here, in either declaration order.
                       // Oracle: MustKeepTheCommandBehaviorRegistrationWhenAGenericCollateralInterfaceIsDeclaredAfterIt
                       // and MustNotRegisterABehaviorUnderANonGenericCollateralInterface.
                       // Mutation that reddens them: restoring .AsImplementedInterfaces() in place of this .As(...)
                       // (observed under Scrutor 7.0.0).
                       // Declared-before order:
                       // MustKeepTheCommandBehaviorRegistrationWhenAGenericCollateralInterfaceIsDeclaredBeforeIt stays
                       // green under that mutation, because the collateral descriptor is added first and Replace deletes
                       // it when the ICommandBehavior<> descriptor lands, leaving the correct end state. It reddens only
                       // if the UsingRegistrationStrategy line is also deleted (observed under 7.0.0).
                       // Residual, unpinned: a class implementing ICommandBehavior<> at two different closings still
                       // yields two service types and still self-deletes all but the last. That is pre-existing and no
                       // test pins it. Replace(ReplacementBehavior.ServiceType) is not the escape, because distinct
                       // behaviors legitimately share ICommandBehavior<> as their service type.
                       // Residual, unpinned: "at most one" is none for a scanned class whose own generic arity differs
                       // from ICommandBehavior<>'s - e.g. Behavior<TMessage, TDependency> : ICommandBehavior<TMessage>,
                       // which passes this method's validation and matches the scan, yet registers nothing. Inherited,
                       // not introduced: .AsImplementedInterfaces() drops that same interface for the same reason. The
                       // arity gate here reproduced Scrutor 3.3.0's; Scrutor 7.0.0's skips the comparison when the
                       // interface has no open generic parameters, so for Behavior<TFirst, TSecond> :
                       // ICommandBehavior<SomeCommand> .AsImplementedInterfaces() maps ICommandBehavior<> onto
                       // Behavior<,> while this gate selects nothing (observed with a scratch probe under 7.0.0). The
                       // shipped .As(...) path applies only this gate, so the Scrutor change does not reach it. No test
                       // pins either case. Rejecting such a type at composition time would be a new breaking failure
                       // for callers it silently no-ops for today, and supporting the mapping is a separate feature;
                       // both are product decisions, not taken here.
                       .As(behavior => GetCommandBehaviorServiceTypes(behavior))
                       .WithTransientLifetime());

            return services;
        }

        private static IEnumerable<Type> GetCommandBehaviorServiceTypes(Type behaviorType)
            => behaviorType.GetImplementedInterfacesThatMatchOpenGenericType(typeof(ICommandBehavior<>))
                .Where(commandBehaviorInterface => !behaviorType.IsGenericType
                    || behaviorType.GetGenericArguments().Length == commandBehaviorInterface.GetGenericArguments().Length)
                .Select(commandBehaviorInterface => behaviorType.IsGenericTypeDefinition
                    ? commandBehaviorInterface.GetGenericTypeDefinition()
                    : commandBehaviorInterface);

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
