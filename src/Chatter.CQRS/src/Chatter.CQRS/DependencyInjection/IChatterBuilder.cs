using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.CQRS.DependencyInjection
{
    /// <summary>
    /// A builder class used to register depedencies for the Chatter.CQRS library.
    /// </summary>
    public interface IChatterBuilder
    {
        /// <summary>
        /// An <see cref="IServiceCollection"/> containing all Chatter.CQRS dependency registrations.
        /// </summary>
        IServiceCollection Services { get; }
        IConfiguration Configuration { get; }
        IAssemblySourceFilter AssemblySourceFilter { get; }
    }
}
