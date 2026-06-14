using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class ReliabilityStoreLifetimeException : Exception
    {
        public ReliabilityStoreLifetimeException(Type serviceType, ServiceLifetime lifetime)
            : base($"Custom reliability store '{serviceType.Name}' is registered as {lifetime}, which is not supported. " +
                   $"Register the custom reliability store as Scoped or Singleton so the framework can forward the secondary " +
                   $"facet at the same lifetime and keep both facets on the same instance. " +
                   $"Alternatively, register both facets independently as separate descriptors.")
        {
        }

        public ReliabilityStoreLifetimeException(Type serviceType, ServiceLifetime lifetime, Exception inner)
            : base($"Custom reliability store '{serviceType.Name}' is registered as {lifetime}, which is not supported. " +
                   $"Register the custom reliability store as Scoped or Singleton so the framework can forward the secondary " +
                   $"facet at the same lifetime and keep both facets on the same instance. " +
                   $"Alternatively, register both facets independently as separate descriptors.",
                   inner)
        {
        }
    }
}
