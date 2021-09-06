using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    public class AssemblySourceFilterBuilder
    {
        private IAssemblyFilterSourceProvider _searchAssemblyProvider;
        private string _namespaceSelector;
        private List<Assembly> _explicitAssemblies = new List<Assembly>();

        private AssemblySourceFilterBuilder() { }
        private AssemblySourceFilterBuilder(IAssemblyFilterSourceProvider searchAssemblyProvider) => _searchAssemblyProvider = searchAssemblyProvider;

        public static AssemblySourceFilterBuilder New() => new AssemblySourceFilterBuilder();

        /// <summary>
        /// Sets the provider that returns the base set of assemblies to be filtered by the <see cref="AssemblySourceFilter"/>. <see cref="CurrentAppDomainAssemblyProvider"/> is used by default.
        /// </summary>
        /// <param name="assemblySourceProvider">The assembly provider</param>
        public static AssemblySourceFilterBuilder WithAssemblySourceProvider(IAssemblyFilterSourceProvider assemblySourceProvider)
        {
            _ = assemblySourceProvider ?? throw new ArgumentNullException(nameof(assemblySourceProvider));
            return new AssemblySourceFilterBuilder(assemblySourceProvider);
        }

        /// <summary>
        /// Sets a namespace selector which is used to filter assemblies by types with matching namespaces or assemblies with matching names.
        /// Supports '*' and '?' wildcard values.
        /// </summary>
        /// <param name="namespaceSelector">The namespace filter value</param>
        public AssemblySourceFilterBuilder WithNamespaceSelector(string namespaceSelector)
        {
            _namespaceSelector = namespaceSelector;
            return this;
        }

        /// <summary>
        /// Sets a namespace selector which is used to filter assemblies by types with matching namespaces or assemblies with matching names.
        /// Supports '*' and '?' wildcard values.
        /// </summary>
        /// <param name="namespaceSelector">An action that builds a namespace selector</param>
        public AssemblySourceFilterBuilder WithNamespaceSelector(Action<NamespaceSelectorBuilder> namespaceSelector)
        {
            var builder = NamespaceSelectorBuilder.New();
            namespaceSelector(builder);
            return this.WithNamespaceSelector(builder.Build());
        }

        /// <summary>
        /// Adds explicit assemblies via marker types used for message handler registration
        /// </summary>
        /// <param name="markerTypes">The marker types used to select assemblies</param>
        public AssemblySourceFilterBuilder WithMarkerTypes(params Type[] markerTypes)
        {
            var assembliesFromMarkerTypes = GetAssembliesFromMarkerTypes(markerTypes?.ToArray());
            _explicitAssemblies = _explicitAssemblies.Union(assembliesFromMarkerTypes).ToList();
            return this;
        }

        /// <summary>
        /// Adds explicit assemblies to be used for message handler registration
        /// </summary>
        /// <param name="assemblies">The assemblies to search</param>
        public AssemblySourceFilterBuilder WithExplicitAssemblies(params Assembly[] assemblies)
        {
            _explicitAssemblies = _explicitAssemblies.Union(assemblies).ToList();
            return this;
        }

        public AssemblySourceFilter Build()
        {
            _searchAssemblyProvider ??= CurrentAppDomainAssemblyProvider.Default;
            return new AssemblySourceFilter(_searchAssemblyProvider, _namespaceSelector, _explicitAssemblies);
        }

        private IEnumerable<Assembly> GetAssembliesFromMarkerTypes(params Type[] markerTypeSelector)
            => markerTypeSelector?.Select(t => t.Assembly) ?? new List<Assembly>();
    }
}
