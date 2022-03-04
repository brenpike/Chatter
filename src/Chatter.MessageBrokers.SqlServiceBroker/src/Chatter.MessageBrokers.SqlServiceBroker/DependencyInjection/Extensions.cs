using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.MessageBrokers.SqlServiceBroker;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving.CircuitBreaker;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving.Retry;
using Chatter.MessageBrokers.SqlServiceBroker.Sending;
using System;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        public static SqlServiceBrokerOptionsBuilder AddSqlServiceBrokerOptions(this IServiceCollection services)
            => new SqlServiceBrokerOptionsBuilder(services);

        public static IChatterBuilder AddSqlServiceBroker(this IChatterBuilder builder, Action<SqlServiceBrokerOptionsBuilder> optionsBuilder = null)
        {
            var optBuilder = builder.Services.AddSqlServiceBrokerOptions();
            optionsBuilder?.Invoke(optBuilder);
            var options = optBuilder.Build();

            builder.Services.AddIfNotRegistered<SqlServiceBrokerReceiver>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<SqlServiceBrokerSenderFactory>(ServiceLifetime.Singleton);

            builder.Services.AddIfNotRegistered<SqlServiceBrokerSender>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<SqlServiceBrokerReceiverFactory>(ServiceLifetime.Singleton);

            builder.Services.AddSingleton<ICircuitBreakerExceptionPredicatesProvider, SqlCircuitBreakerExceptionPredicatesProvider>();
            builder.Services.AddSingleton<IRetryExceptionPredicatesProvider, SqlRetryExceptionPredicatesProvider>();

            builder.Services.AddSingleton<IMessagingInfrastructure>(sp =>
            {
                var sender = sp.GetRequiredService<SqlServiceBrokerSenderFactory>();
                var receiver = sp.GetRequiredService<SqlServiceBrokerReceiverFactory>();
                return new MessagingInfrastructure(SSBMessageContext.InfrastructureType, receiver, sender);
            });

            builder.Services.AddScoped<IBrokeredMessageBodyConverter, JsonUnicodeBodyConverter>();
            builder.Services.AddSingleton(options);

            return builder;
        }

        public static SqlServiceBrokerOptionsBuilder AddQueueReceiver<TMessage>(this SqlServiceBrokerOptionsBuilder builder,
                                                                                string queueName,
                                                                                string errorQueuePath = null,
                                                                                string description = null,
                                                                                TransactionMode? transactionMode = null,
                                                                                string deadLetterServicePath = null,
                                                                                int maxReceiveAttempts = 10)
            where TMessage : class, IMessage
        {
            builder.Services.AddReceiver<TMessage>(queueName, errorQueuePath, description, queueName, transactionMode, SSBMessageContext.InfrastructureType, deadLetterServicePath, maxReceiveAttempts);
            return builder;
        }
    }
}
