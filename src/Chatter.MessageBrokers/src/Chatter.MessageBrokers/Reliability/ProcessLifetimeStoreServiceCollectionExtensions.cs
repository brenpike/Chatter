using Chatter.CQRS.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Chatter.MessageBrokers.Reliability
{
    internal static class ProcessLifetimeStoreServiceCollectionExtensions
    {
        /// <summary>
        /// Registers a shipped <see cref="IProcessLifetimeStore"/> as the process-lifetime default for
        /// <typeparamref name="TService"/>, unless the application already registered its own.
        /// </summary>
        /// <remarks>
        /// INVARIANT: this delegates to the presence-gated
        /// <see cref="ServiceCollectionExtensions.AddIfNotRegistered{TService, TImplementation}(IServiceCollection, ServiceLifetime)"/>
        /// rather than replacing it, so a custom store registered before <c>AddMessageBrokers</c> still wins and keeps
        /// the lifetime the application chose. Only the shipped default's lifetime is decided here.
        /// The method is internal because C# forbids a public method whose generic constraint names a less-accessible
        /// type (CS0703), and <see cref="IProcessLifetimeStore"/> is internal.
        /// </remarks>
        internal static IServiceCollection AddProcessLifetimeDefault<TService, TImplementation>(this IServiceCollection services)
            where TService : class
            where TImplementation : class, TService, IProcessLifetimeStore
            => services.AddIfNotRegistered<TService, TImplementation>(ServiceLifetime.Singleton);
    }
}
