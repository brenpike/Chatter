using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System;

namespace Chatter.MessageBrokers.Configuration
{
    internal sealed class BuiltOptions<TOptions> : IOptions<TOptions>, IOptionsSnapshot<TOptions>, IOptionsMonitor<TOptions> where TOptions : class
    {
        private readonly TOptions _builtOptions;

        public BuiltOptions(TOptions builtOptions)
        {
            _builtOptions = builtOptions;
        }

        public TOptions Value => _builtOptions;

        public TOptions CurrentValue => _builtOptions;

        // INVARIANT: this context has no named options, so every name - including null and empty - resolves the one
        // built instance. A name-keyed lookup that produced anything else would reintroduce the second, unvalidated
        // options object this type exists to eliminate.
        public TOptions Get(string name) => _builtOptions;

        // INVARIANT: built options never reload - configuration is bound into the instance once, at build time - so the
        // change registration is inert. One shared no-op registration is therefore safe to hand every caller and safe
        // to dispose any number of times.
        public IDisposable OnChange(Action<TOptions, string> onChange) => NoOpChangeRegistration.Instance;

        private sealed class NoOpChangeRegistration : IDisposable
        {
            internal static readonly NoOpChangeRegistration Instance = new NoOpChangeRegistration();

            private NoOpChangeRegistration() { }

            public void Dispose() { }
        }
    }

    internal static class BuiltOptionsServiceCollectionExtensions
    {
        public static IServiceCollection AddBuiltOptions<TOptions>(this IServiceCollection services, TOptions builtOptions) where TOptions : class
        {
            var sharedFacet = new BuiltOptions<TOptions>(builtOptions);

            services.AddSingleton(builtOptions);
            // INVARIANT: the facets are REPLACED, not appended. A second Build() on the same IServiceCollection would
            // otherwise leave the earlier facet registered alongside the new one, so enumerating a facet would still
            // hand out the orphaned earlier options instance. These closed generic registrations also take precedence
            // over the open generic IOptions<>, IOptionsSnapshot<> and IOptionsMonitor<> descriptors AddOptions()
            // registers, regardless of which was registered first.
            services.Replace(ServiceDescriptor.Singleton<IOptions<TOptions>>(sharedFacet));
            services.Replace(ServiceDescriptor.Singleton<IOptionsSnapshot<TOptions>>(sharedFacet));
            services.Replace(ServiceDescriptor.Singleton<IOptionsMonitor<TOptions>>(sharedFacet));

            return services;
        }
    }
}
