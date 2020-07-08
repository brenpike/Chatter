using Microsoft.Extensions.DependencyInjection;

namespace Chatter.CQRS.DependencyInjection
{
    /// <summary>
    /// A builder class used to register depedencies the Chatter library
    /// </summary>
    public sealed class ChatterBuilder : IChatterBuilder
    {
        private readonly IServiceCollection _services;

        ///<inheritdoc/>
        IServiceCollection IChatterBuilder.Services => _services;

        private ChatterBuilder(IServiceCollection services)
        {
            _services = services;
        }

        /// <summary>
        /// Factory method to create a new instance of <see cref="ChatterBuilder"/>.
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IChatterBuilder Create(IServiceCollection services)
            => new ChatterBuilder(services);
    }
}
