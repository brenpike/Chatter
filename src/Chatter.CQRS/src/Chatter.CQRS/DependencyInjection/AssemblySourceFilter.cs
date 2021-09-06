using System;
using System.Collections.Generic;
using System.IO.Enumeration;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    public class AssemblySourceFilter
    {
        public IAssemblyFilterSourceProvider AssemblySourceProvider { get; }
        public string NamespaceSelector { get; }
        public IEnumerable<Assembly> ExplictAssemblies { get; }

        internal AssemblySourceFilter(IAssemblyFilterSourceProvider assemblySourceProvider, string namespaceSelector, IEnumerable<Assembly> explictAssemblies)
        {
            AssemblySourceProvider = assemblySourceProvider ?? throw new ArgumentNullException(nameof(assemblySourceProvider));
            NamespaceSelector = namespaceSelector;
            ExplictAssemblies = explictAssemblies ?? new List<Assembly>();
        }

        /// <summary>
        /// Gets the assemblies which match filter criteria.
        /// </summary>
        /// <returns>The enumerable of assemblies</returns>
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
