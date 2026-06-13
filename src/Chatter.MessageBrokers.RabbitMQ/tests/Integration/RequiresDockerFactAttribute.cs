using System;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // A [Fact] that runs only when Docker is reachable. When Docker is absent the fact is SKIPPED at
    // discovery time (Skip is set before the test runs) so a plain `dotnet test` on a Docker-free machine
    // reports these as skipped, never failed. The Category=Integration trait is applied at the test-class
    // level so CI can include/exclude with `--filter Category=Integration`. Mirrors the SQL Service Broker /
    // Azure Service Bus RequiresDockerFact attribute.
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RequiresDockerFactAttribute : FactAttribute
    {
        public RequiresDockerFactAttribute()
        {
            if (!DockerEnvironment.IsAvailable)
            {
                Skip = DockerEnvironment.SkipReason;
            }
        }
    }
}
