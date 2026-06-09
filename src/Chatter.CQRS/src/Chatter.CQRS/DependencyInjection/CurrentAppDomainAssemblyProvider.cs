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

        // INVARIANT: dynamic assemblies (e.g. mock/dynamic-proxy assemblies like DynamicProxyGenAssembly2)
        // can never contain Chatter handlers/behaviors and are inherently unscannable; excluding them here
        // stops them from reaching Scrutor's FromAssemblies type enumeration, which throws on dynamic assemblies.
        public IEnumerable<Assembly> GetSourceAssemblies() => AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic);
    }
}
