using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
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
        /// Adds and configured Chatter message broker related capabilities
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used for registration and setup</param>
        /// <param name="markerTypesForRequiredAssemblies">Marker types whose parent assemblies will be used to find <see cref="IBrokeredMessageReceiver{TMessage}"/> for registration.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, params Type[] markerTypesForRequiredAssemblies)
            => AddMessageBrokers(builder, null, markerTypesForRequiredAssemblies);

        /// <summary>
        /// Adds and configured Chatter message broker related capabilities
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used for registration and setup</param>
        /// <param name="messageBrokerOptionsBuilder">A delegate that uses a <see cref="MessageBrokerOptionsBuilder"/> to construct <see cref="MessageBrokerOptions"/></param>
        /// <param name="markerTypesForRequiredAssemblies">Marker types whose parent assemblies will be used to find <see cref="IBrokeredMessageReceiver{TMessage}"/> for registration. Will override any assemblies located via <see cref="AssemblySourceFilter"/> created during Chatter cqrs configuration.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, Action<MessageBrokerOptionsBuilder> messageBrokerOptionsBuilder = null, params Type[] markerTypesForRequiredAssemblies)
            => AddMessageBrokers(builder, messageBrokerOptionsBuilder, b => b.WithMarkerTypes(markerTypesForRequiredAssemblies));

        /// <summary>
        /// Adds and configured Chatter message broker related capabilities
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used for registration and setup</param>
        /// <param name="messageBrokerOptionsBuilder">A delegate that uses a <see cref="MessageBrokerOptionsBuilder"/> to construct <see cref="MessageBrokerOptions"/></param>
        /// <param name="receiverAssemblies">Assemblies that will be used to find <see cref="IBrokeredMessageReceiver{TMessage}"/> for registration. Will override any assemblies located via <see cref="AssemblySourceFilter"/> created during Chatter cqrs configuration.</param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, Action<MessageBrokerOptionsBuilder> messageBrokerOptionsBuilder = null, params Assembly[] receiverAssemblies)
            => AddMessageBrokers(builder, messageBrokerOptionsBuilder, b => b.WithExplicitAssemblies(receiverAssemblies));

        /// <summary>
        /// Adds and configured Chatter message broker related capabilities
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used for registration and setup</param>
        /// <param name="receiverNamespaceSelector">A namespace selector used to find assemblies containing types with matching namespaces or assemblies with matching FullName. Supports '*' and '?' wildcard values. Matching assemblies used to find <see cref="IBrokeredMessageReceiver{TMessage}"/> for registration. Will override any assemblies located via <see cref="AssemblySourceFilter"/> created during Chatter cqrs configuration.</param>
        /// <param name="messageBrokerOptionsBuilder">A delegate that uses a <see cref="MessageBrokerOptionsBuilder"/> to construct <see cref="MessageBrokerOptions"/></param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, string receiverNamespaceSelector, Action<MessageBrokerOptionsBuilder> messageBrokerOptionsBuilder = null)
            => AddMessageBrokers(builder, messageBrokerOptionsBuilder, b => b.WithNamespaceSelector(receiverNamespaceSelector));

        /// <summary>
        /// Adds and configured Chatter message broker related capabilities
        /// </summary>
        /// <param name="builder">A <see cref="IChatterBuilder"/> used for registration and setup</param>
        /// <param name="receiverHandlerSourceBuilder">An optional builder used to define an <see cref="AssemblySourceFilter"/>. Assemblies will be used to find <see cref="IBrokeredMessageReceiver{TMessage}"/> for registration. Will override any assemblies located via <see cref="AssemblySourceFilter"/> created during Chatter cqrs configuration.</param>
        /// <param name="optionsBuilder">A delegate that uses a <see cref="MessageBrokerOptionsBuilder"/> to construct <see cref="MessageBrokerOptions"/></param>
        /// <returns>An <see cref="IChatterBuilder"/> used to configure Chatter capabilities</returns>
        public static IChatterBuilder AddMessageBrokers(this IChatterBuilder builder, Action<MessageBrokerOptionsBuilder> optionsBuilder = null, Action<AssemblySourceFilterBuilder> receiverHandlerSourceBuilder = null)
        {
            var filter = builder.AssemblySourceFilter;
            if (receiverHandlerSourceBuilder != null)
            {
                var filterBuilder = AssemblySourceFilterBuilder.New();
                receiverHandlerSourceBuilder(filterBuilder);
                filter = filterBuilder.Build();
            }
            var assemblies = filter.Apply();

            var messageBrokerOptionsBuilder = builder.Services.AddMessageBrokerOptions(builder.Configuration);
            optionsBuilder?.Invoke(messageBrokerOptionsBuilder);
            MessageBrokerOptions options = messageBrokerOptionsBuilder.Build();

            builder.Services.AddSingleton<IMessagingInfrastructureProvider, MessagingInfrastructureProvider>();

            builder.Services.Replace<IExternalDispatcher, BrokeredMessageDispatcher>(ServiceLifetime.Scoped);
            builder.Services.AddScoped<IBrokeredMessageReceiverFactory, BrokeredMessageReceiverFactory>();
            builder.Services.AddScoped<IBrokeredMessageDispatcher, BrokeredMessageDispatcher>();
            builder.Services.AddIfNotRegistered<IBrokeredMessagePathBuilder, DefaultBrokeredMessagePathBuilder>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<IBrokeredMessageAttributeDetailProvider, BrokeredMessageAttributeProvider>(ServiceLifetime.Scoped);

            builder.Services.AddIfNotRegistered<ICircuitBreaker, CircuitBreaker>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<ICircuitBreakerStateStore, InMemoryCircuitBreakerStateStore>(ServiceLifetime.Scoped);

            builder.Services.AddScoped<IFailedReceiveRecoverer, FailedReceiveRecoverer>();
            builder.Services.AddIfNotRegistered<IRecoveryAction, ErrorQueueDispatcher>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<IDelayedRecoveryStrategy, NoDelayRecovery>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<ICriticalFailureNotifier, CriticalFailureEventDispatcher>(ServiceLifetime.Scoped);

            builder.Services.AddIfNotRegistered<IMessageIdGenerator, GuidIdGenerator>(ServiceLifetime.Scoped);

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
            builder.Services.AddScoped<IBrokeredMessageBodyConverter, TextPlainBodyConverter>();
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
                                                                        string senderPath = null,
                                                                        TransactionMode? transactionMode = null,
                                                                        string infrastructureType = "",
                                                                        string deadletterQueuePath = null)
            where TMessage : class, IMessage
        {
            builder.Services.AddReceiver<TMessage>(receiverPath, errorQueuePath, description, senderPath, transactionMode, infrastructureType, deadletterQueuePath);
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
                builder.Services.AddReceiverImpl(receiverType.Item2, receiverType.Item1);
            }
            return builder;
        }

        public static IServiceCollection AddReceiver<TMessage>(this IServiceCollection services,
                                                               string receiverPath,
                                                               string errorQueuePath = null,
                                                               string description = null,
                                                               string senderPath = null,
                                                               TransactionMode? transactionMode = null,
                                                               string infrastructureType = "",
                                                               string deadletterQueuePath = null)
            where TMessage : class, IMessage
        {
            var messageType = typeof(TMessage);
            var attr = messageType.TryGetBrokeredMessageAttribute();

            if (attr != null)
            {
                throw new InvalidOperationException($"Receiver for {messageType.Name} has already been registered via {nameof(BrokeredMessageAttribute)}");
            }

            var options = new ReceiverOptions()
            {
                SendingPath = senderPath,
                MessageReceiverPath = receiverPath,
                ErrorQueuePath = errorQueuePath,
                Description = description,
                TransactionMode = transactionMode,
                InfrastructureType = infrastructureType,
                DeadLetterQueuePath = deadletterQueuePath
            };

            services.AddReceiverImpl<TMessage>(options);
            return services;
        }

        private static void AddReceiverImpl(this IServiceCollection services, ReceiverOptions options, Type closedBrokeredMessageReceiverInterface, Type closedConcreteBrokeredMessageReceiver, Type closedConcreteReceiverBackgroundService)
        {
            services.AddScoped(closedBrokeredMessageReceiverInterface, closedConcreteBrokeredMessageReceiver);
            services.AddScoped(closedConcreteReceiverBackgroundService, sp =>
            {
                return Activator.CreateInstance(closedConcreteReceiverBackgroundService,
                                                options,
                                                sp);
            });
            services.AddSingleton(typeof(IHostedService), sp =>
            {
                using var scope = sp.CreateScope();
                return scope.ServiceProvider.GetRequiredService(closedConcreteReceiverBackgroundService);
            });
        }

        private static void AddReceiverImpl(this IServiceCollection services, Type messageTypeToReceive, ReceiverOptions options)
        {
            var closedBrokeredMessageReceiverInterface = typeof(IBrokeredMessageReceiver<>).MakeGenericType(messageTypeToReceive);
            var closedConcreteBrokeredMessageReceiver = typeof(BrokeredMessageReceiver<>).MakeGenericType(messageTypeToReceive);
            var closedConcreteReceiverBackgroundService = typeof(BrokeredMessageReceiverBackgroundService<>).MakeGenericType(messageTypeToReceive);
            services.AddReceiverImpl(options, closedBrokeredMessageReceiverInterface, closedConcreteBrokeredMessageReceiver, closedConcreteReceiverBackgroundService);
        }

        private static void AddReceiverImpl<TMessage>(this IServiceCollection services, ReceiverOptions options)
            where TMessage : class, IMessage
        {
            var closedBrokeredMessageReceiverInterface = typeof(IBrokeredMessageReceiver<TMessage>);
            var closedConcreteBrokeredMessageReceiver = typeof(BrokeredMessageReceiver<TMessage>);
            var closedConcreteReceiverBackgroundService = typeof(BrokeredMessageReceiverBackgroundService<TMessage>);
            services.AddReceiverImpl(options, closedBrokeredMessageReceiverInterface, closedConcreteBrokeredMessageReceiver, closedConcreteReceiverBackgroundService);
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
                    SendingPath = attr.SendingPath,
                    InfrastructureType = attr.InfrastructureType,
                    DeadLetterQueuePath = attr.DeadletterQueueName
                };

                yield return (options, messageType);
            }
        }
    }
}
