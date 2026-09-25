using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
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
    /// </summary>
    internal sealed class HandlerScanRecord
    {
        private readonly List<Assembly> _scannedAssemblies = new List<Assembly>();

        /// <summary>
        /// Every assembly recorded so far, each once, in the order first recorded.
        /// </summary>
        internal IReadOnlyCollection<Assembly> ScannedAssemblies => _scannedAssemblies;

        /// <summary>
        /// Adds the assemblies one <c>AddChatterCqrs</c> call scanned, skipping any already recorded.
        /// </summary>
        internal void Record(IEnumerable<Assembly> scannedAssemblies)
        {
            foreach (var scannedAssembly in scannedAssemblies)
            {
                if (!_scannedAssemblies.Contains(scannedAssembly))
                {
                    _scannedAssemblies.Add(scannedAssembly);
                }
            }
        }

        /// <summary>
        /// Returns the record <paramref name="services"/> carries, adding one when it carries none.
        /// INVARIANT: a collection carries exactly one record, however many <c>AddChatterCqrs</c> calls it receives.
        /// Oracle: WhenAddingChatterCqrs.MustRecordTheHandlerScanOnceHoweverManyTimesChatterCqrsIsAdded.
        /// Mutation that reddens it: adding a new record unconditionally.
        /// </summary>
        internal static HandlerScanRecord GetOrAdd(IServiceCollection services)
        {
            var existingRecord = Find(services);

            if (existingRecord is not null)
            {
                return existingRecord;
            }

            var addedRecord = new HandlerScanRecord();
            services.Add(new ServiceDescriptor(typeof(HandlerScanRecord), addedRecord));
            return addedRecord;
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
