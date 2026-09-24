using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    internal class CurrentAppDomainAssemblyProvider : IAssemblyFilterSourceProvider
    {
        private CurrentAppDomainAssemblyProvider() {}

        internal static CurrentAppDomainAssemblyProvider Default => new CurrentAppDomainAssemblyProvider();

        // INVARIANT: dynamic assemblies (e.g. mock/dynamic-proxy assemblies like DynamicProxyGenAssembly2) are
        // excluded from the scan set. Pinned by WhenGettingSourceAssemblies.MustExcludeDynamicAssemblies;
        // removing the `!assembly.IsDynamic` predicate reddens it (observed). The reason: an assembly emitted
        // at runtime can never contain a consumer's handlers or behaviors, so excluding it keeps the scan set
        // deterministic and cheap. No test pins that reason.
        public IEnumerable<Assembly> GetSourceAssemblies() => AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic);
    }
}
