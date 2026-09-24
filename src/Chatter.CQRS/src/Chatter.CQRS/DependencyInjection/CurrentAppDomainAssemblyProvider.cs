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
        // removing the `!assembly.IsDynamic` predicate reddens it (observed). The reason is a deliberate
        // exclusion, not an impossibility: a dynamic assembly CAN hold a type this scan would otherwise
        // register - a Moq or Castle proxy of ICommandHandler<T> is a non-abstract class implementing a
        // handler interface - and which dynamic assemblies exist depends on whatever proxy code has already
        // run, so scanning them would make an unbounded scan set vary between runs of the same program. A
        // handler emitted at runtime is registered explicitly instead. No test pins that reason.
        public IEnumerable<Assembly> GetSourceAssemblies() => AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic);
    }
}
