using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace Chatter.MessageBrokers.SqlServiceBroker.DependencyInjection
{
    /// <summary>
    /// The transport settings the first successful <c>AddSqlServiceBroker</c> call on an <see cref="IServiceCollection"/>
    /// registered, recorded on that collection as a singleton instance so that a later call compares its options with
    /// the transport that call registered rather than with whatever <see cref="SqlServiceBrokerOptions"/> descriptor
    /// the collection carries.
    /// INVARIANT: a later call compares with this record, never with a <see cref="SqlServiceBrokerOptions"/>
    /// descriptor, and the record is a frozen value: it holds a <see cref="SqlServiceBrokerTransportSettings"/>
    /// snapshot read when the record is written, so a change to the options instance afterwards does not move the
    /// settings later calls compare with. Oracles, in WhenAddingSqlServiceBroker:
    /// MustAcceptAFirstCallWhenSqlServiceBrokerOptionsWereRegisteredWithoutIt,
    /// MustRefuseADivergentSecondCallWhenOptionsWereAlsoRegisteredWithoutAnInstance and
    /// MustAddNoOptionsDescriptorForAnEquivalentSecondCallWhenOptionsWereAlsoRegisteredWithoutAnInstance (both rows each),
    /// MustAcceptALaterCallWithTheRegisteredValuesAfterTheCallerChangesItsOptionsInstance and
    /// MustRefuseALaterCallWithTheChangedValuesAfterTheCallerChangesItsOptionsInstance.
    /// Mutation: <see cref="Find"/> snapshotting the instance held by the last <see cref="SqlServiceBrokerOptions"/>
    /// descriptor instead of reading the record. Measured: those seven go red, and nothing else in this test project.
    /// NOT covered: a record descriptor copied into another collection is shared by both collections. No test pins
    /// that a call on either leaves the shared record unchanged, because such a copy also carries the options
    /// descriptor, and the tests cannot tell a comparison with one from a comparison with the other.
    /// </summary>
    internal sealed class SqlServiceBrokerTransportRegistration
    {
        private SqlServiceBrokerTransportRegistration(SqlServiceBrokerTransportSettings transportSettings)
            => TransportSettings = transportSettings;

        /// <summary>
        /// The transport settings as they were when the record was written.
        /// </summary>
        internal SqlServiceBrokerTransportSettings TransportSettings { get; }

        /// <summary>
        /// Adds a record of <paramref name="transportSettings"/> to <paramref name="services"/>.
        /// </summary>
        internal static void Record(IServiceCollection services, SqlServiceBrokerTransportSettings transportSettings)
            => services.Add(new ServiceDescriptor(typeof(SqlServiceBrokerTransportRegistration),
                                                  new SqlServiceBrokerTransportRegistration(transportSettings)));

        /// <summary>
        /// Returns the last record <paramref name="services"/> carries, or <see langword="null"/> when it carries none.
        /// Reading it writes nothing.
        /// INVARIANT: when a collection carries several records, the last one is read, matching the last
        /// <see cref="SqlServiceBrokerOptions"/> singleton descriptor Microsoft DI resolves when each record arrived with
        /// its options descriptor. Oracle: WhenAddingSqlServiceBroker.MustCompareWithTheLastRegisteredTransportWhenDescriptorsOfAnotherRegistrationWereCopiedIn.
        /// Mutation: returning the first record. Measured: that fact alone goes red, and nothing else in this test project.
        /// </summary>
        internal static SqlServiceBrokerTransportRegistration Find(IServiceCollection services)
            => services.LastOrDefault(IsRecord)?.ImplementationInstance as SqlServiceBrokerTransportRegistration;

        private static bool IsRecord(ServiceDescriptor descriptor)
            => descriptor.ServiceType == typeof(SqlServiceBrokerTransportRegistration)
               && descriptor.ImplementationInstance is SqlServiceBrokerTransportRegistration;
    }
}
