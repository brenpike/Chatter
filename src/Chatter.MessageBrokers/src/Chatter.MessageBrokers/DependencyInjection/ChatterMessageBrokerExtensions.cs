using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Slips;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class ChatterMessageBrokerExtensions
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static MessageBrokerOptionsBuilder AddMessageBrokerOptions(this IServiceCollection services)
        {
            return new MessageBrokerOptionsBuilder(services);
        }

        /// <summary>
        /// Initializes a <see cref="ChatterBuilder"/> and registers all dependencies.
        /// Registers all <see cref="BrokeredMessageReceiver{TMessage}"/> and automatically starts receiving if configured to do so.
        /// Registers all routers.
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used registration and setup</param>
        /// <param name="markerTypesForRequiredAssemblies">The <see cref="Type"/>s from assemblies that are required for registering receivers</param>
        /// <param name="messageBrokerOptionsBuilder">A delegate that uses a <see cref="MessageBrokerOptionsBuilder"/> to construct <see cref="MessageBrokerOptions"/></param>
        /// <returns>An instance of <see cref="IChatterBuilder"/>.</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, Action<MessageBrokerOptionsBuilder> messageBrokerOptionsBuilder = null, params Type[] markerTypesForRequiredAssemblies)
        {
            var assemblies = markerTypesForRequiredAssemblies.GetAssembliesFromMarkerTypes();
            return AddMessageBrokers(builder, assemblies, messageBrokerOptionsBuilder);
        }

        /// <summary>
        /// Initializes a <see cref="ChatterBuilder"/> and registers all dependencies.
        /// Registers all <see cref="BrokeredMessageReceiver{TMessage}"/> and automatically starts receiving if configured to do so.
        /// Registers all routers.
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used registration and setup</param>
        /// <param name="markerTypesForRequiredAssemblies">The <see cref="Type"/>s from assemblies that are required for registering receivers</param>
        /// <returns>An instance of <see cref="IChatterBuilder"/>.</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, params Type[] markerTypesForRequiredAssemblies)
        {
            return AddMessageBrokers(builder, null, markerTypesForRequiredAssemblies);
        }

        /// <summary>
        /// Initializes a <see cref="ChatterBuilder"/> and registers all dependencies.
        /// Registers all <see cref="BrokeredMessageReceiver{TMessage}"/> and automatically starts receiving if configured to do so.
        /// Registers all routers.
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used registration and setup</param>
        /// <param name="assemblies">The assemblies that are required for registering receivers</param>
        /// <returns>An instance of <see cref="IChatterBuilder"/>.</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, IEnumerable<Assembly> assemblies, Action<MessageBrokerOptionsBuilder> optionsBuilder = null)
        {
            var messageBrokerOptionsBuilder = builder.Services.AddMessageBrokerOptions();
            optionsBuilder?.Invoke(messageBrokerOptionsBuilder);
            MessageBrokerOptions options = messageBrokerOptionsBuilder.Build();

            builder.Services.AddTransient<IBrokeredMessageDispatcher, BrokeredMessageDispatcher>();

            builder.Services.AddSingleton<ITransactionalBrokeredMessageOutbox, InMemoryBrokeredMessageOutbox>();
            builder.Services.AddTransient<IForwardMessages, ForwardingRouter>();
            builder.Services.AddTransient<IRouteCompensationMessages, CompensateRouter>();
            builder.Services.AddTransient<IRouteReplyToMessages, ReplyToRouter>();

            if (options?.Reliability?.OutboxEnabled ?? false)
            {
                builder.Services.Decorate<IMessageDispatcher, TransactionalOutboxMessageDispatcherDecorator>();
                builder.Services.AddSingleton<IRouteMessages, OutboxMessageRouter>();
                builder.Services.AddHostedService<BrokeredMessageOutboxProcessor>();
            }
            else
            {
                builder.Services.AddTransient<IRouteMessages, MessageRouter>();
            }

            builder.Services.Decorate<IMessageDispatcher, RoutingSlipMessageDispatcherDecorator>(); //TODO: we'll only want to add this if routing slips are added
            builder.AddReceivers(assemblies);

            builder.Services.AddSingleton<IBodyConverterFactory, BodyConverterFactory>();
            builder.Services.AddSingleton<IBrokeredMessageBodyConverter, JsonBodyConverter>();

            return builder;
        }

        /// <summary>
        /// Attempts to get a <see cref="BrokeredMessageAttribute"/> from a class.
        /// </summary>
        /// <param name="memberInfo">A class decorated with <see cref="BrokeredMessageAttribute"/>.</param>
        /// <returns>The <see cref="BrokeredMessageAttribute"/> if it exists; null otherwise./></returns>
        /// <remarks>
        /// This method does not throw an exception if the supplied <code>memberInfo</code> is not decorated with a <see cref="BrokeredMessageAttribute"/> and instead returns null.
        /// </remarks>
        public static BrokeredMessageAttribute TryGetBrokeredMessageAttribute(this MemberInfo memberInfo)
            => (BrokeredMessageAttribute)memberInfo.GetCustomAttribute(typeof(BrokeredMessageAttribute));

        /// <summary>
        /// Gets a <see cref="BrokeredMessageAttribute"/> from a class.
        /// </summary>
        /// <param name="memberInfo">A class decorated with <see cref="BrokeredMessageAttribute"/>.</param>
        /// <returns>The <see cref="BrokeredMessageAttribute"/>.</returns>
        /// <exception cref="BrokeredMessageAttributeNotFoundException"><code>memberInfo</code> is not decorated with <see cref="BrokeredMessageAttribute"/>.</exception>
        /// <remarks>
        /// This method throws a <see cref="BrokeredMessageAttributeNotFoundException"/> if the supplied memberInfo is not decorated with a <see cref="BrokeredMessageAttribute"/>.
        /// </remarks>
        public static BrokeredMessageAttribute GetBrokeredMessageAttribute(this MemberInfo memberInfo)
            => memberInfo.TryGetBrokeredMessageAttribute()
            ?? throw new BrokeredMessageAttributeNotFoundException(memberInfo);

        /// <summary>
        /// Finds all class in all assemblies within the current app domain that are decorated with <see cref="BrokeredMessageAttribute"/> and have a non-null <see cref="BrokeredMessageAttribute.ReceiverName"/>.
        /// </summary>
        /// <returns>A read only list of classes that are decorated with <see cref="BrokeredMessageAttribute"/> and implement <see cref="IMessage"/>.</returns>
        private static IReadOnlyList<Type> FindBrokeredMessagesWithReceiversInAssembliesByType(IEnumerable<Assembly> assemblies)
        {
            return assemblies.SelectMany(assemblies => assemblies.GetTypes()
                    .Where(type => type.GetInterfaces()
                        .Any(i => i == typeof(IMessage)))
                    .Where(type => type.IsDefined(typeof(BrokeredMessageAttribute), false))
                    .Where(type => type.TryGetBrokeredMessageAttribute()?.ReceiverName != null)
                    ).ToList();
        }

        /// <summary>
        /// Scans all assemblies for classes decorated with <see cref="BrokeredMessageAttribute"/> and have a non-null <see cref="BrokeredMessageAttribute.ReceiverName"/>. Registers:
        /// </summary>
        /// <param name="builder">The singleton <see cref="ChatterBuilder"/> instance used for registration.</param>
        /// <returns>The singleton <see cref="IChatterBuilder"/> instance.</returns>
        private static IChatterBuilder AddReceivers(this IChatterBuilder builder, IEnumerable<Assembly> assemblies)
        {
            var messages = FindBrokeredMessagesWithReceiversInAssembliesByType(assemblies);
            foreach (var receiverType in GetAllReceiverTypes(messages))
            {
                builder.Services.AddSingleton(receiverType.Item1, receiverType.Item2);
                builder.Services.AddSingleton(typeof(IHostedService), sp => sp.GetRequiredService(receiverType.Item1));
            }
            return builder;
        }

        private static IEnumerable<(Type, Type, bool)> GetAllReceiverTypes(IReadOnlyList<Type> messages)
        {
            foreach (var messageType in messages)
            {
                var messageTypeInterfaces = messageType.GetInterfaces();
                if (!messageTypeInterfaces.Contains(typeof(IMessage)))
                {
                    continue;
                }

                var attr = messageType.TryGetBrokeredMessageAttribute();

                if (attr == null)
                {
                    throw new ArgumentException($"Unable to start receiving messages. {messageType.Name} is not decorated with a { nameof(BrokeredMessageAttribute)}.");
                }

                var concreteReceiverThatCloses = typeof(BrokeredMessageReceiver<>).MakeGenericType(messageType);
                var interfaceThatCloses = typeof(IBrokeredMessageReceiver<>).MakeGenericType(messageType);

                yield return (interfaceThatCloses, concreteReceiverThatCloses, attr.AutoReceiveMessages);
            }
        }
    }
}
