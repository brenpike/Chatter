using Chatter.CQRS;
using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.MessageBrokers.SqlServiceBroker;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.DependencyInjection;
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
            // INVARIANT: every refusal this method raises lands before its first write to builder.Services, so a
            // refused call runs none of its deferred registrations: the options delegate defers its receivers and
            // registrations through DeferRegistration, Build() and the divergence guard run next, the writes follow,
            // and RegisterPendingRegistrations runs last, in the order the registrations were deferred. Oracles, in
            // WhenAddingSqlServiceBroker: MustLeaveTheServiceCollectionExactlyAsItWasWhenBuildRefusesTheOptions and
            // MustWriteNoDeferredRegistrationWhenBuildRefusesTheOptions (three Build() refusals each, each after an
            // AddQueueReceiver), MustLeaveTheServiceCollectionExactlyAsItWasWhenADivergentCallIsRefused,
            // MustLeaveTheDiscoveredReceiversUnchangedWhenADivergentCallIsRefused and
            // MustWriteNoDeferredRegistrationWhenADivergentCallIsRefused (divergent refusals),
            // MustNotWriteADeferredRegistrationWhileTheOptionsDelegateRuns and
            // MustWriteARegistrationDeferredAfterAQueueReceiverAfterThatReceiversDescriptors (deferral and order).
            // Mutations, measured: AddQueueReceiver calling services.AddReceiver immediately reddens the six Build()
            // refusal rows and the first two divergent facts; DeferRegistration running the registration immediately
            // reddens those eight, the third divergent fact, the deferral fact and the order fact; running
            // RegisterPendingRegistrations before the divergence guard reddens the three divergent facts and the
            // order fact; running it directly after the guard, or iterating it in reverse, reddens only the order
            // fact. Each mutation reddens nothing else in this test project.
            // NOT covered: an extension that writes through the public SqlServiceBrokerOptionsBuilder.Services
            // property from inside the delegate mutates the collection immediately, and no test pins that case.
            var optBuilder = builder.Services.AddSqlServiceBrokerOptions();
            optionsBuilder?.Invoke(optBuilder);
            var options = optBuilder.Build();
            var candidateTransport = SqlServiceBrokerTransportSettings.SnapshotOf(options);
            var registeredTransport = SqlServiceBrokerTransportRegistration.Find(builder.Services);
            RefuseDivergentOptions(registeredTransport, candidateTransport);

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
            if (registeredTransport is null)
            {
                builder.Services.AddSingleton(options);
                SqlServiceBrokerTransportRegistration.Record(builder.Services, candidateTransport);
            }
            else if (!builder.Services.Any(descriptor => descriptor.ServiceType == typeof(SqlServiceBrokerOptions)))
            {
                // A record can reach a collection without the options descriptor that came with it - a ServiceDescriptor
                // is freely copyable - and that collection reads as already configured. The options this call built match
                // the record setting by setting, because RefuseDivergentOptions just compared them, so registering them
                // here leaves the collection able to resolve the SqlServiceBrokerOptions the receiver, sender and
                // connection source require. TryAddSingleton is unreachable by name from this file: see the collision
                // recorded in Chatter.MessageBrokers.Reliability.EntityFramework.Extensions.RemoveReliabilityRetention.
                builder.Services.AddSingleton(options);
            }

            optBuilder.RegisterPendingRegistrations();

            return builder;
        }

        // INVARIANT: after any sequence of successful AddSqlServiceBroker calls on one collection, this method has
        // written exactly one SqlServiceBrokerOptions descriptor and one SqlServiceBrokerTransportRegistration. The
        // record is written by the first call that finds none, and the options with it; a later call either matches
        // the recorded transport setting by setting or is refused, and an accepted later call writes the options only
        // when the collection carries no SqlServiceBrokerOptions descriptor at all, which is how a collection that
        // received a copied record without its paired options descriptor still resolves its options. Oracles, in
        // WhenAddingSqlServiceBroker: MustRegisterOneSqlServiceBrokerOptionsWhenASecondCallConfiguresEquivalentOptions
        // and MustAddNoOptionsDescriptorForAnEquivalentSecondCallWhenOptionsWereAlsoRegisteredWithoutAnInstance
        // (options written once), MustRecordTheTransportOnceWhenASecondCallConfiguresEquivalentOptions (record written
        // once), MustRegisterTheOptionsWhenOnlyTheTransportRecordWasCopiedIn (a call that finds a record but no
        // options descriptor writes the options and no second record),
        // MustRefuseASecondCallWhoseConnectionStringDiverges and MustNameEveryDivergingSettingWhenASecondCallIsRefused
        // (divergent call refused). Mutations, measured: registering the options unconditionally - equivalently,
        // dropping the else branch's descriptor check so it runs whatever the collection carries - reddens the first
        // fact and both Theory rows; deleting that else branch reddens only
        // MustRegisterTheOptionsWhenOnlyTheTransportRecordWasCopiedIn; recording the transport unconditionally reddens
        // only the record fact; deleting
        // the RefuseDivergentOptions call reddens nine: the two refusal facts, the two divergent-call no-mutation
        // facts, both rows of MustRefuseADivergentSecondCallWhenOptionsWereAlsoRegisteredWithoutAnInstance,
        // MustRefuseALaterCallWithTheChangedValuesAfterTheCallerChangesItsOptionsInstance,
        // MustRefuseADivergentCallOnACollectionTheRegisteredDescriptorsWereCopiedInto and
        // MustCompareWithTheLastRegisteredTransportWhenDescriptorsOfAnotherRegistrationWereCopiedIn. Each mutation
        // reddens nothing else in this test project.
        // NOT covered, and no test pins it: a SqlServiceBrokerOptions descriptor registered outside this method after
        // its first call, by instance, type or factory, is the one Microsoft DI resolves, while later calls still
        // compare with the record; nothing inspects registrations made outside this method.
        private static void RefuseDivergentOptions(SqlServiceBrokerTransportRegistration registeredTransport, SqlServiceBrokerTransportSettings candidateTransport)
        {
            if (registeredTransport is null)
            {
                return;
            }

            var divergences = SqlServiceBrokerTransportSettings.FindDivergences(registeredTransport.TransportSettings, candidateTransport);
            if (divergences.Count == 0)
            {
                return;
            }

            var describedDivergences = string.Join("; ", divergences.Select(DescribeDivergence));
            throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, DivergentOptionsMessage, describedDivergences));
        }

        private static string DescribeDivergence(SqlServiceBrokerTransportSettings.Divergence divergence)
            => $"{divergence.SettingName} (registered: {divergence.RegisteredValue}, this call: {divergence.CandidateValue})";

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
            // a throwaway collection, so the deferred copy repeats a registration that has already succeeded once with
            // the same arguments. Oracle: MustRefuseAQueueReceiverForABrokeredMessageDecoratedTypeWhenItIsAdded in
            // WhenAddingSqlServiceBroker. Mutation: deleting the throwaway-collection call. Measured: that fact alone goes red.
            RegisterReceiver(new ServiceCollection());
            return builder.DeferRegistration(RegisterReceiver);
        }
    }
}
