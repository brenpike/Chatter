using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Reliability;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Configuration;
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
        public static MessageBrokerOptionsBuilder AddMessageBrokerOptions(this IServiceCollection services, IConfiguration configuration)
            => new MessageBrokerOptionsBuilder(services, configuration);

        /// <summary>
        /// Initializes a <see cref="ChatterBuilder"/> and registers all dependencies.
        /// Registers all <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> and automatically starts receiving if configured to do so.
        /// Registers all routers.
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used registration and setup</param>
        /// <param name="markerTypesForRequiredAssemblies">The <see cref="Type"/>s from assemblies that are required for registering receivers</param>
        /// <returns>An instance of <see cref="IChatterBuilder"/>.</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, params Type[] markerTypesForRequiredAssemblies)
            => AddMessageBrokers(builder, null, markerTypesForRequiredAssemblies);

        /// <summary>
        /// Initializes a <see cref="ChatterBuilder"/> and registers all dependencies.
        /// Registers all <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> and automatically starts receiving if configured to do so.
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
        /// Registers all <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> and automatically starts receiving if configured to do so.
        /// Registers all routers.
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used registration and setup</param>
        /// <param name="assemblies">The assemblies that are required for registering receivers</param>
        /// <returns>An instance of <see cref="IChatterBuilder"/>.</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, IEnumerable<Assembly> assemblies, Action<MessageBrokerOptionsBuilder> optionsBuilder = null)
        {
            assemblies = assemblies.Union(builder.MarkerAssemblies);
            var messageBrokerOptionsBuilder = builder.Services.AddMessageBrokerOptions(builder.Configuration);
            optionsBuilder?.Invoke(messageBrokerOptionsBuilder);
            MessageBrokerOptions options = messageBrokerOptionsBuilder.Build();

            builder.Services.AddScoped<IBrokeredMessageReceiverFactory, BrokeredMessageReceiverFactory>();
            builder.Services.AddScoped<IBrokeredMessageDispatcher, BrokeredMessageDispatcher>();
            builder.Services.AddIfNotRegistered<IBrokeredMessagePathBuilder, DefaultBrokeredMessagePathBuilder>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<IBrokeredMessageAttributeDetailProvider, BrokeredMessageAttributeProvider>(ServiceLifetime.Scoped);

            builder.Services.AddScoped<IFailedReceiveRecoverer, FailedReceiveRecoverer>();
            builder.Services.AddIfNotRegistered<IRecoveryAction, ErrorQueueDispatcher>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<IDelayedRecovery, NoDelayRecovery>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<ICriticalFailureNotifier, CriticalFailureEventDispatcher>(ServiceLifetime.Scoped);

            builder.Services.AddScoped<IForwardMessages, ForwardingRouter>();
            builder.Services.AddScoped<IReplyRouter, ReplyRouter>();

            builder.Services.AddScoped<IOutboxProcessor, OutboxProcessor>();
            builder.Services.AddIfNotRegistered<IBrokeredMessageOutbox, InMemoryBrokeredMessageOutbox>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<IBrokeredMessageInbox, InMemoryBrokeredMessageInbox>(ServiceLifetime.Scoped);

            if (options?.Reliability?.EnableOutboxPollingProcessor ?? false)
            {
                builder.Services.AddHostedService<BrokeredMessageOutboxProcessor>();
            }

            if (options?.Reliability?.RouteMessagesToOutbox ?? false)
            {
                builder.Services.AddIfNotRegistered<IRouteBrokeredMessages, OutboxBrokeredMessageRouter>(ServiceLifetime.Scoped);
            }
            else
            {
                builder.Services.AddIfNotRegistered<IRouteBrokeredMessages, BrokeredMessageRouter>(ServiceLifetime.Scoped);
            }

            builder.AddAllReceivers(assemblies);

            builder.Services.AddScoped<IBodyConverterFactory, BodyConverterFactory>();
            builder.Services.AddScoped<IBrokeredMessageBodyConverter, JsonBodyConverter>();

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
            => assemblies.SelectMany(assemblies => assemblies.GetTypes()
                         .Where(type => type.GetInterfaces()
                             .Any(i => i == typeof(IMessage)))
                         .Where(type => type.IsDefined(typeof(BrokeredMessageAttribute), false))
                         .Where(type => type.TryGetBrokeredMessageAttribute()?.ReceiverName != null)
                    ).ToList();

        public static MessageBrokerOptionsBuilder AddReceiver<TMessage>(this MessageBrokerOptionsBuilder builder,
                                                                        string receiverPath,
                                                                        string errorQueuePath = null,
                                                                        string description = null,
                                                                        TransactionMode? transactionMode = null)
            where TMessage : class, IMessage
        {
            var options = new ReceiverOptions()
            {
                MessageReceiverPath = receiverPath,
                ErrorQueuePath = errorQueuePath,
                Description = description,
                TransactionMode = transactionMode
            };

            AddReceiver(builder.Services, typeof(TMessage), options);
            return builder;
        }

        /// <summary>
        /// Scans all assemblies for classes decorated with <see cref="BrokeredMessageAttribute"/> and have a non-null <see cref="BrokeredMessageAttribute.ReceiverName"/>. Registers:
        /// </summary>
        /// <param name="builder">The singleton <see cref="ChatterBuilder"/> instance used for registration.</param>
        /// <returns>The singleton <see cref="IChatterBuilder"/> instance.</returns>
        private static IChatterBuilder AddAllReceivers(this IChatterBuilder builder, IEnumerable<Assembly> assemblies)
        {
            var messages = FindBrokeredMessagesWithReceiversInAssembliesByType(assemblies);
            foreach (var receiverType in GetAllReceiverTypes(messages))
            {
                AddReceiver(builder.Services, receiverType.Item2, receiverType.Item1);
            }
            return builder;
        }

        private static void AddReceiver(IServiceCollection services, Type receiverConcreteType, ReceiverOptions options)
        {
            var concreteReceiverThatCloses = typeof(BrokeredMessageReceiverBackgroundService<>).MakeGenericType(receiverConcreteType);
            services.AddScoped(concreteReceiverThatCloses, sp =>
            {
                var factory = sp.GetRequiredService<IBrokeredMessageReceiverFactory>();
                return Activator.CreateInstance(concreteReceiverThatCloses,
                                                options,
                                                factory);
            });

            services.AddSingleton(typeof(IHostedService), sp =>
            {
                using var scope = sp.CreateScope();
                return scope.ServiceProvider.GetRequiredService(concreteReceiverThatCloses);
            });
        }

        private static IEnumerable<(ReceiverOptions, Type)> GetAllReceiverTypes(IReadOnlyList<Type> messages)
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

                var options = new ReceiverOptions()
                {
                    MessageReceiverPath = attr.ReceiverName,
                    ErrorQueuePath = attr.ErrorQueueName,
                    Description = attr.MessageDescription,
                    TransactionMode = null,
                    SendingPath = attr.MessageName
                };

                yield return (options, messageType);
            }
        }
    }
}
