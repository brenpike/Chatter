using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
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
        private readonly IEnumerable<Assembly> _markerAssemblies;

        ///<inheritdoc/>
        IServiceCollection IChatterBuilder.Services => _services;
        ///<inheritdoc/>
        IConfiguration IChatterBuilder.Configuration => _configuration;
        ///<inheritdoc/>
        IEnumerable<Assembly> IChatterBuilder.MarkerAssemblies => _markerAssemblies;

        private ChatterBuilder(IServiceCollection services, IConfiguration configuration, IEnumerable<Assembly> markerAssemblies)
        { 
            _services = services;
            _configuration = configuration;
            _markerAssemblies = markerAssemblies;
        }

        /// <summary>
        /// Factory method to create a new instance of <see cref="ChatterBuilder"/>.
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IChatterBuilder Create(IServiceCollection services, IConfiguration configuration, IEnumerable<Assembly> markerAssemblies)
            => new ChatterBuilder(services, configuration, markerAssemblies);
    }
}
