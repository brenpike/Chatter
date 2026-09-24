using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    /// <summary>
    /// A builder class used to register depedencies the Chatter library
    /// </summary>
    public sealed class ChatterBuilder : IChatterBuilder
    {
        private readonly IServiceCollection _services;
        private readonly IConfiguration _configuration;
        private readonly IAssemblySourceFilter _assemblySourceFilter;

        ///<inheritdoc/>
        IServiceCollection IChatterBuilder.Services => _services;
        ///<inheritdoc/>
        IConfiguration IChatterBuilder.Configuration => _configuration;
        ///<inheritdoc/>
        IAssemblySourceFilter IChatterBuilder.AssemblySourceFilter => _assemblySourceFilter;

        /// <summary>
        /// The assemblies <c>CqrsExtensions.AddChatterCqrs</c> scanned for handler registration, or
        /// <see langword="null"/> when this builder was created without them.
        /// NOTE: why this set is captured once rather than re-read from the filter is stated at the INVARIANT on the
        /// materialization in <c>CqrsExtensions.AddChatterCqrs</c>.
        /// </summary>
        internal IReadOnlyCollection<Assembly> ScannedAssemblies { get; }

        private ChatterBuilder(IServiceCollection services, IConfiguration configuration, IAssemblySourceFilter filter)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _assemblySourceFilter = filter ?? throw new ArgumentNullException(nameof(filter));
        }

        private ChatterBuilder(IServiceCollection services, IConfiguration configuration, IAssemblySourceFilter filter, IReadOnlyCollection<Assembly> scannedAssemblies)
            : this(services, configuration, filter)
            => ScannedAssemblies = scannedAssemblies ?? throw new ArgumentNullException(nameof(scannedAssemblies));

        /// <summary>
        /// Factory method to create a new instance of <see cref="ChatterBuilder"/>.
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IChatterBuilder Create(IServiceCollection services, IConfiguration configuration, IAssemblySourceFilter filter)
            => new ChatterBuilder(services, configuration, filter);

        internal static IChatterBuilder Create(IServiceCollection services, IConfiguration configuration, IAssemblySourceFilter filter, IReadOnlyCollection<Assembly> scannedAssemblies)
            => new ChatterBuilder(services, configuration, filter, scannedAssemblies);
    }
}
