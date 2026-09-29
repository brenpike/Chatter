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
using System.Globalization;
using System.Linq;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class Extensions
    {
        public static SqlServiceBrokerOptionsBuilder AddSqlServiceBrokerOptions(this IServiceCollection services)
            => new SqlServiceBrokerOptionsBuilder(services);

        private const string DivergentOptionsMessage =
            "AddSqlServiceBroker supports one SQL Server Service Broker transport configuration per host, and this call's " +
            "Service Broker Options differ from the SqlServiceBrokerOptions already registered: {0}. Receiver settings stay " +
            "per receiver: pass the queue, error queue, transaction mode, dead-letter service and maximum receive attempts to " +
            "each AddQueueReceiver call. Per-receiver transport options are tracked in https://github.com/brenpike/Chatter/issues/542.";

        public static IChatterBuilder AddSqlServiceBroker(this IChatterBuilder builder, Action<SqlServiceBrokerOptionsBuilder> optionsBuilder = null)
        {
            // INVARIANT: every refusal this method raises lands before its first write to builder.Services: the
            // options delegate enqueues receivers rather than registering them, Build() and the divergence guard run
            // next, and the writes follow. Oracles: MustLeaveTheServiceCollectionExactlyAsItWasWhenBuildRefusesTheOptions
            // (three Build() refusals, each after an AddQueueReceiver), MustLeaveTheServiceCollectionExactlyAsItWasWhenADivergentCallIsRefused
            // and MustLeaveTheDiscoveredReceiversUnchangedWhenADivergentCallIsRefused, in WhenAddingSqlServiceBroker.
            // Mutation: AddQueueReceiver calling services.AddReceiver immediately instead of enqueuing. Measured: all
            // three Theory rows and both divergent-call facts go red, and nothing else in this test project.
            // NOT covered: an extension that writes through the public SqlServiceBrokerOptionsBuilder.Services
            // property from inside the delegate mutates the collection immediately, and no test pins that case.
            var optBuilder = builder.Services.AddSqlServiceBrokerOptions();
            optionsBuilder?.Invoke(optBuilder);
            var options = optBuilder.Build();
            var registeredOptions = EffectiveRegistration(builder.Services, typeof(SqlServiceBrokerOptions))?
                .ImplementationInstance as SqlServiceBrokerOptions;
            RefuseDivergentOptions(registeredOptions, options);

            builder.Services.AddIfNotRegistered<ISqlConnectionSource, SqlClientConnectionSource>(ServiceLifetime.Scoped);

            builder.Services.AddIfNotRegistered<SqlServiceBrokerReceiver>(ServiceLifetime.Scoped);
            builder.Services.AddIfNotRegistered<SqlServiceBrokerSender>(ServiceLifetime.Scoped);

            builder.Services.AddSingleton<ICircuitBreakerExceptionPredicatesProvider, SqlCircuitBreakerExceptionPredicatesProvider>();
            builder.Services.AddSingleton<IRetryExceptionPredicatesProvider, SqlRetryExceptionPredicatesProvider>();

            builder.Services.AddSingleton<IMessagingInfrastructure>(sp =>
            {
                // Folded receiver/dispatcher factory captured directly in this infrastructure's
                // descriptor (NOT resolved from the container by the shared MessagingInfrastructureFactory
                // type), so each broker keeps its own factory under multi-broker registration. Each
                // delegate opens a DI scope, resolves the scoped infrastructure service, and disposes the
                // scope — reproducing the former SqlServiceBrokerReceiverFactory / SqlServiceBrokerSenderFactory
                // behavior exactly.
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                var infrastructureFactory = new MessagingInfrastructureFactory(
                    () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        return scope.ServiceProvider.GetRequiredService<SqlServiceBrokerReceiver>();
                    },
                    () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        return scope.ServiceProvider.GetRequiredService<SqlServiceBrokerSender>();
                    });
                return new MessagingInfrastructure(SSBMessageContext.InfrastructureType, infrastructureFactory, infrastructureFactory);
            });

            builder.Services.AddSingleton<IBrokeredMessageBodyConverter, JsonUnicodeBodyConverter>();
            if (registeredOptions is null)
            {
                builder.Services.AddSingleton(options);
            }

            optBuilder.RegisterPendingReceivers();

            return builder;
        }

        // INVARIANT: after any sequence of successful AddSqlServiceBroker calls whose options are registered as an
        // instance, the collection holds exactly one SqlServiceBrokerOptions descriptor: a later call either matches
        // the registered options setting by setting and adds none, or is refused. Oracles, in WhenAddingSqlServiceBroker:
        // MustRegisterOneSqlServiceBrokerOptionsWhenASecondCallConfiguresEquivalentOptions (equivalent call adds none),
        // MustRefuseASecondCallWhoseConnectionStringDiverges and MustNameEveryDivergingSettingWhenASecondCallIsRefused
        // (divergent call refused). Mutations, measured: registering options unconditionally reddens only the first
        // fact; deleting the RefuseDivergentOptions call reddens the two refusal facts plus the two divergent-call
        // no-mutation facts, and nothing else in this test project. A SqlServiceBrokerOptions descriptor registered
        // by type or factory has no instance to compare, so it reads as absent and this call appends its own, and no
        // test pins that case.
        private static void RefuseDivergentOptions(SqlServiceBrokerOptions registeredOptions, SqlServiceBrokerOptions candidateOptions)
        {
            if (registeredOptions is null)
            {
                return;
            }

            var divergences = SqlServiceBrokerOptionsEquivalence.FindDivergences(registeredOptions, candidateOptions);
            if (divergences.Count == 0)
            {
                return;
            }

            var describedDivergences = string.Join("; ", divergences.Select(DescribeDivergence));
            throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, DivergentOptionsMessage, describedDivergences));
        }

        private static string DescribeDivergence(SqlServiceBrokerOptionsEquivalence.Divergence divergence)
            => $"{divergence.PropertyName} (registered: {divergence.RegisteredValue}, this call: {divergence.CandidateValue})";

        // The descriptor a single-service request resolves to: Microsoft DI resolves it from the LAST descriptor
        // registered for the service type, so the guard compares against the options the container would hand out.
        private static ServiceDescriptor EffectiveRegistration(IServiceCollection services, Type serviceType)
            => services.LastOrDefault(descriptor => descriptor.ServiceType == serviceType);

        public static SqlServiceBrokerOptionsBuilder AddQueueReceiver<TMessage>(this SqlServiceBrokerOptionsBuilder builder,
                                                                                string queueName,
                                                                                string errorQueuePath = null,
                                                                                string description = null,
                                                                                TransactionMode? transactionMode = null,
                                                                                string deadLetterServicePath = null,
                                                                                int maxReceiveAttempts = 10)
            where TMessage : class, IMessage
        {
            void RegisterReceiver(IServiceCollection services)
                => services.AddReceiver<TMessage>(queueName, errorQueuePath, description, queueName, transactionMode, SSBMessageContext.InfrastructureType, deadLetterServicePath, maxReceiveAttempts);

            // INVARIANT: AddReceiver's refusals are raised here, at call time, by running the same registration against
            // a throwaway collection, so the enqueued copy repeats a registration that has already succeeded once with
            // the same arguments. Oracle: MustRefuseAQueueReceiverForABrokeredMessageDecoratedTypeWhenItIsAdded in
            // WhenAddingSqlServiceBroker. Mutation: deleting the throwaway-collection call. Measured: that fact alone goes red.
            RegisterReceiver(new ServiceCollection());
            builder.EnqueueReceiverRegistration(RegisterReceiver);
            return builder;
        }
    }
}
