using System;
using System.Collections.Generic;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    internal class CurrentAppDomainAssemblyProvider : IAssemblyFilterSourceProvider
    {
        private CurrentAppDomainAssemblyProvider() {}

        internal static CurrentAppDomainAssemblyProvider Default => new CurrentAppDomainAssemblyProvider();

        public IEnumerable<Assembly> GetSourceAssemblies() => AppDomain.CurrentDomain.GetAssemblies();
    }
}
