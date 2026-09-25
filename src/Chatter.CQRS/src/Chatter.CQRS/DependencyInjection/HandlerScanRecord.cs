using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    /// <summary>
    /// The assemblies every <c>CqrsExtensions.AddChatterCqrs</c> call on one <see cref="IServiceCollection"/> scanned
    /// for handler registration, recorded on that collection as a singleton instance so that
    /// <c>CqrsExtensions.ThrowOnDuplicateCommandHandlers</c> probes the set the registration scanned.
    /// INVARIANT: the record is keyed on the <see cref="IServiceCollection"/>, not on the builder. The collection is
    /// the only object every <c>AddChatterCqrs</c> call on one container shares, so a handler one call registers and
    /// a later call displaces is still probed, and it is the only object the public <see cref="IChatterBuilder"/>
    /// contract obliges every implementation, a wrapper included, to expose.
    /// Oracles: WhenThrowingOnDuplicateCommandHandlers.MustReportCompetingHandlersAcrossTwoAddChatterCqrsCallsOnOneServiceCollection,
    /// MustReportCompetingHandlersWhenCheckedOnTheBuilderFromTheEarlierCall and
    /// MustProbeTheScanRecordThroughABuilderThatOnlyForwardsItsServices.
    /// Mutation that reddens them: keying the set on the builder, carried on <see cref="ChatterBuilder"/> and read
    /// through an <c>as ChatterBuilder</c> cast.
    /// INVARIANT: a record is a frozen value; every write forks a new record, so a record descriptor copied into another
    /// collection carries a snapshot no later <c>AddChatterCqrs</c> call, on either collection, can change.
    /// Oracles: WhenThrowingOnDuplicateCommandHandlers.MustNotReportAHandlerScannedOnlyByACollectionItsDescriptorsWereCopiedInto
    /// and WhenAddingChatterCqrs.MustReplaceTheScanRecordRatherThanMutateItWhenChatterCqrsIsAddedAgain.
    /// Mutation that reddens them: adding the newly scanned assemblies to the existing record in place instead of
    /// replacing its descriptor.
    /// </summary>
    internal sealed class HandlerScanRecord
    {
        private HandlerScanRecord(IList<Assembly> scannedAssemblies)
            => ScannedAssemblies = new ReadOnlyCollection<Assembly>(scannedAssemblies);

        /// <summary>
        /// Every assembly recorded so far, each once, in the order first recorded.
        /// </summary>
        internal IReadOnlyCollection<Assembly> ScannedAssemblies { get; }

        /// <summary>
        /// Records on <paramref name="services"/> the assemblies one <c>AddChatterCqrs</c> call scanned: the record
        /// descriptor <paramref name="services"/> carries is replaced, at its index, by one holding a new record of the
        /// assemblies already recorded followed by those in <paramref name="scannedAssemblies"/> not yet recorded; a
        /// collection that carries no record gains one.
        /// INVARIANT: a collection carries exactly one record descriptor after any serialized sequence of
        /// <c>AddChatterCqrs</c> calls. Oracle: WhenAddingChatterCqrs.MustRecordTheHandlerScanOnceHoweverManyTimesChatterCqrsIsAdded,
        /// which composes its calls sequentially; concurrent composition on one collection is unsupported (ADR-0039 R4).
        /// Mutation that reddens it: adding a new record descriptor unconditionally.
        /// INVARIANT: the record descriptor keeps the index it was first added at.
        /// Oracle: WhenAddingChatterCqrs.MustKeepTheScanRecordAtItsPositionWhenChatterCqrsIsAddedAgain.
        /// Mutation that reddens it: writing the new descriptor with <c>ServiceCollectionDescriptorExtensions.Replace</c>,
        /// which appends it.
        /// </summary>
        internal static void Record(IServiceCollection services, IEnumerable<Assembly> scannedAssemblies)
        {
            var recordIndex = IndexOfRecord(services);
            var recordedAssemblies = new List<Assembly>();

            if (recordIndex >= 0)
            {
                recordedAssemblies.AddRange(((HandlerScanRecord)services[recordIndex].ImplementationInstance).ScannedAssemblies);
            }

            foreach (var scannedAssembly in scannedAssemblies)
            {
                if (!recordedAssemblies.Contains(scannedAssembly))
                {
                    recordedAssemblies.Add(scannedAssembly);
                }
            }

            var recordDescriptor = new ServiceDescriptor(typeof(HandlerScanRecord), new HandlerScanRecord(recordedAssemblies));

            if (recordIndex >= 0)
            {
                services[recordIndex] = recordDescriptor;
            }
            else
            {
                services.Add(recordDescriptor);
            }
        }

        private static int IndexOfRecord(IServiceCollection services)
        {
            for (var descriptorIndex = 0; descriptorIndex < services.Count; descriptorIndex++)
            {
                if (services[descriptorIndex].ServiceType == typeof(HandlerScanRecord)
                    && services[descriptorIndex].ImplementationInstance is HandlerScanRecord)
                {
                    return descriptorIndex;
                }
            }

            return -1;
        }

        /// <summary>
        /// Returns the record <paramref name="services"/> carries, or <see langword="null"/> when
        /// <paramref name="services"/> is <see langword="null"/> or carries no record.
        /// </summary>
        internal static HandlerScanRecord Find(IServiceCollection services)
            => services?.Where(descriptor => descriptor.ServiceType == typeof(HandlerScanRecord))
                        .Select(descriptor => descriptor.ImplementationInstance)
                        .OfType<HandlerScanRecord>()
                        .FirstOrDefault();
    }
}
