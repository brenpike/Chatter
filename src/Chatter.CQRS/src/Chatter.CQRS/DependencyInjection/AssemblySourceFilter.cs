using System;
using System.Collections.Generic;
using System.IO.Enumeration;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    public class AssemblySourceFilter
    {
        public IAssemblySourceProvider AssemblySourceProvider { get; }
        public string NamespaceSelector { get; }
        public IEnumerable<Assembly> ExplictAssemblies { get; }

        internal AssemblySourceFilter(IAssemblySourceProvider assemblySourceProvider, string namespaceSelector, IEnumerable<Assembly> explictAssemblies)
        {
            AssemblySourceProvider = assemblySourceProvider ?? throw new ArgumentNullException(nameof(assemblySourceProvider));
            NamespaceSelector = namespaceSelector;
            ExplictAssemblies = explictAssemblies ?? new List<Assembly>();
        }

        /// <summary>
        /// Gets the assemblies which match filter criteria.
        /// </summary>
        /// <returns>The enumerable of assemblies</returns>
        public IEnumerable<Assembly> Filter()
            => ExplictAssemblies.Union(GetAssembliesThatMatchNamespaceSelector());

        private IEnumerable<Assembly> GetAssembliesThatMatchNamespaceSelector()
            => AssemblySourceProvider.GetSourceAssemblies().Where(f => f.GetTypes()
                .Any(t => DoesTypeHaveMatchingNamespace(t) || DoesTypeHaveMatchingAssemblyName(t)));

        private bool DoesTypeHaveMatchingNamespace(Type t)
            => IsMatchingNamespaceSelector(t.Namespace);

        private bool DoesTypeHaveMatchingAssemblyName(Type t)
            => IsMatchingNamespaceSelector(t.Assembly?.FullName);

        private bool IsMatchingNamespaceSelector(string comparator)
            => string.IsNullOrWhiteSpace(NamespaceSelector)
                   || FileSystemName.MatchesSimpleExpression(NamespaceSelector, comparator ?? string.Empty, true);
    }
}
