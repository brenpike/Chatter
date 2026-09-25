using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        /// Records on <paramref name="services"/> the assemblies one <c>AddChatterCqrs</c> call scanned: the first record
        /// descriptor <paramref name="services"/> carries is replaced, at its index, by one holding a new record of the
        /// assemblies every record descriptor it carries holds, as <see cref="FindScannedAssemblies"/> unites them,
        /// followed by those in <paramref name="scannedAssemblies"/> not among them, and every other record descriptor is
        /// removed; a collection that carries no record gains one.
        /// INVARIANT: a collection carries exactly one record descriptor after any serialized sequence of
        /// <c>AddChatterCqrs</c> calls, whatever record descriptors were copied into it before the last call.
        /// Oracles: WhenAddingChatterCqrs.MustRecordTheHandlerScanOnceHoweverManyTimesChatterCqrsIsAdded and
        /// MustCollapseEveryScanRecordTheCollectionCarriesIntoOne, which compose their calls sequentially; concurrent
        /// composition on one collection is unsupported (ADR-0039 R4).
        /// Mutations that redden them: adding a new record descriptor unconditionally; leaving the other record
        /// descriptors in place (observed to redden MustCollapseEveryScanRecordTheCollectionCarriesIntoOne).
        /// INVARIANT: the record descriptor keeps the index it was first added at.
        /// Oracle: WhenAddingChatterCqrs.MustKeepTheScanRecordAtItsPositionWhenChatterCqrsIsAddedAgain.
        /// Mutation that reddens it: writing the new descriptor with <c>ServiceCollectionDescriptorExtensions.Replace</c>,
        /// which appends it.
        /// </summary>
        internal static void Record(IServiceCollection services, IEnumerable<Assembly> scannedAssemblies)
        {
            var recordIndexes = IndexesOfRecords(services);
            var recordedAssemblies = UniteRecordedAssemblies(services, recordIndexes);
            AddUnrecordedAssemblies(recordedAssemblies, scannedAssemblies);
            var recordDescriptor = new ServiceDescriptor(typeof(HandlerScanRecord), new HandlerScanRecord(recordedAssemblies));

            if (recordIndexes.Count == 0)
            {
                services.Add(recordDescriptor);
                return;
            }

            services[recordIndexes[0]] = recordDescriptor;

            for (var recordPosition = recordIndexes.Count - 1; recordPosition > 0; recordPosition--)
            {
                services.RemoveAt(recordIndexes[recordPosition]);
            }
        }

        /// <summary>
        /// Returns every assembly the record descriptors <paramref name="services"/> carries hold, each once: the records
        /// in descriptor order, and each record's assemblies in the order it recorded them; or <see langword="null"/>
        /// when <paramref name="services"/> is <see langword="null"/> or carries no record.
        /// INVARIANT: the result is a pure function of the record descriptors <paramref name="services"/> carries, all of
        /// them, and reading it writes nothing; only <see cref="Record"/> collapses several records into one.
        /// Oracles: WhenThrowingOnDuplicateCommandHandlers.MustProbeEveryScanRecordTheCollectionCarries,
        /// MustLeaveTheApplicationServiceCollectionUntouched and MustLeaveACollectionCarryingSeveralScanRecordsUntouched.
        /// Mutations observed to redden them: returning the first record's assemblies only (the first oracle);
        /// collapsing the records through <see cref="Record"/> on every read (the second); collapsing them only when
        /// there are several (the third).
        /// </summary>
        internal static IReadOnlyCollection<Assembly> FindScannedAssemblies(IServiceCollection services)
        {
            if (services == null)
            {
                return null;
            }

            var recordIndexes = IndexesOfRecords(services);

            if (recordIndexes.Count == 0)
            {
                return null;
            }

            return new ReadOnlyCollection<Assembly>(UniteRecordedAssemblies(services, recordIndexes));
        }

        private static List<int> IndexesOfRecords(IServiceCollection services)
        {
            var recordIndexes = new List<int>();

            for (var descriptorIndex = 0; descriptorIndex < services.Count; descriptorIndex++)
            {
                if (services[descriptorIndex].ServiceType == typeof(HandlerScanRecord)
                    && services[descriptorIndex].ImplementationInstance is HandlerScanRecord)
                {
                    recordIndexes.Add(descriptorIndex);
                }
            }

            return recordIndexes;
        }

        private static List<Assembly> UniteRecordedAssemblies(IServiceCollection services, IEnumerable<int> recordIndexes)
        {
            var recordedAssemblies = new List<Assembly>();

            foreach (var recordIndex in recordIndexes)
            {
                AddUnrecordedAssemblies(recordedAssemblies, ((HandlerScanRecord)services[recordIndex].ImplementationInstance).ScannedAssemblies);
            }

            return recordedAssemblies;
        }

        private static void AddUnrecordedAssemblies(List<Assembly> recordedAssemblies, IEnumerable<Assembly> assemblies)
        {
            foreach (var assembly in assemblies)
            {
                if (!recordedAssemblies.Contains(assembly))
                {
                    recordedAssemblies.Add(assembly);
                }
            }
        }
    }
}
