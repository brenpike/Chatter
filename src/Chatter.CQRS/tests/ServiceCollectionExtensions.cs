using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace Chatter.CQRS.Tests
{
    public static class ServiceCollectionExtensions
    {
        public static ServiceDescriptor GetServiceDescriptorByImplementationType(this IServiceCollection serviceCollection, Type implementationType)
            => serviceCollection.Where(sd => sd.ImplementationType == implementationType).SingleOrDefault();
    }
}
