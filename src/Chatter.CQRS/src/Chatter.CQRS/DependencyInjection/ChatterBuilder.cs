using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

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

        private ChatterBuilder(IServiceCollection services, IConfiguration configuration, IAssemblySourceFilter filter)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _assemblySourceFilter = filter ?? throw new ArgumentNullException(nameof(filter));
        }

        /// <summary>
        /// Factory method to create a new instance of <see cref="ChatterBuilder"/>.
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IChatterBuilder Create(IServiceCollection services, IConfiguration configuration, IAssemblySourceFilter filter)
            => new ChatterBuilder(services, configuration, filter);
    }
}
