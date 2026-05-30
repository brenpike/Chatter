using System;
using System.Collections.Generic;
#if !NETSTANDARD2_0
using System.IO.Enumeration;
#else
using System.Text.RegularExpressions;
#endif
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
#if !NETSTANDARD2_0
                   || FileSystemName.MatchesSimpleExpression(NamespaceSelector, comparator ?? string.Empty, true);
#else
                   || MatchesSimpleExpressionPolyfill(NamespaceSelector, comparator ?? string.Empty);

        /// <summary>
        /// Polyfill for <c>System.IO.Enumeration.FileSystemName.MatchesSimpleExpression</c> (not available in netstandard2.0).
        /// Supports <c>*</c> (zero-or-more chars) and <c>?</c> (exactly one char); whole-string, case-insensitive match.
        /// </summary>
        private static bool MatchesSimpleExpressionPolyfill(string expression, string name)
        {
            // Escape all regex metacharacters in the expression, then restore * and ? wildcards.
            var regexPattern = "^" + Regex.Escape(expression)
                                         .Replace(@"\*", ".*")
                                         .Replace(@"\?", ".") + "$";
            return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
#endif
    }
}
