using System;
using System.Collections.Generic;
using System.IO.Enumeration;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    /// <summary>
    /// Filters a set of source assemblies by applying filters
    /// </summary>
    public interface IAssemblySourceFilter
    {
        /// <summary>
        /// Returns a subset of <see cref="Assembly"/> after applying filter criteria
        /// </summary>
        /// <returns></returns>
        IEnumerable<Assembly> Apply();
    }

    /// <summary>
    /// Filters a set of source <see cref="Assembly"/> defined by <see cref="IAssemblyFilterSourceProvider"/>
    /// </summary>
    public class AssemblySourceFilter : IAssemblySourceFilter
    {
        /// <summary>
        /// The provider which returns the source set of <see cref=" Assembly"/> to be filtered
        /// </summary>
        public IAssemblyFilterSourceProvider AssemblySourceProvider { get; }
        /// <summary>
        /// The value which will be used to match on <see cref="Assembly.FullName"/> or <see cref="Type.Namespace"/> 
        /// </summary>
        public string NamespaceSelector { get; }
        /// <summary>
        /// An enumerable of <see cref="Assembly"/> to be included in the filtered list, regardless on criteria matching
        /// </summary>
        public IEnumerable<Assembly> ExplictAssemblies { get; }

        internal AssemblySourceFilter(IAssemblyFilterSourceProvider assemblySourceProvider, string namespaceSelector, IEnumerable<Assembly> explictAssemblies)
        {
            AssemblySourceProvider = assemblySourceProvider ?? throw new ArgumentNullException(nameof(assemblySourceProvider));
            NamespaceSelector = namespaceSelector;
            ExplictAssemblies = explictAssemblies ?? new List<Assembly>();
        }

        /// <summary>
        /// Applies filter criteria against the <see cref="IAssemblyFilterSourceProvider"/>, returning the <see cref="Assembly"/> that match.
        /// </summary>
        /// <returns>The enumerable of assemblies that match filter criteria and any <see cref="ExplictAssemblies"/></returns>
        public IEnumerable<Assembly> Apply()
            => ExplictAssemblies.Union(GetAssembliesThatMatchNamespaceSelector());

        private IEnumerable<Assembly> GetAssembliesThatMatchNamespaceSelector()
            => AssemblySourceProvider.GetSourceAssemblies().Where(assembly => assembly.GetTypes()
                .Any(type => IsMatchingNamespaceSelector(type.Namespace)) || IsMatchingNamespaceSelector(assembly.FullName));

        private bool IsMatchingNamespaceSelector(string comparator)
            => string.IsNullOrWhiteSpace(NamespaceSelector)
                   || FileSystemName.MatchesSimpleExpression(NamespaceSelector, comparator ?? string.Empty, true);
    }
}
