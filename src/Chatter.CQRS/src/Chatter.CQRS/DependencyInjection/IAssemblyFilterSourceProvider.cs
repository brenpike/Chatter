using System;
using System.Collections.Generic;
using System.Reflection;

namespace Chatter.CQRS.DependencyInjection
{
    public interface IAssemblyFilterSourceProvider
    {
        IEnumerable<Assembly> GetSourceAssemblies();
    }
}
