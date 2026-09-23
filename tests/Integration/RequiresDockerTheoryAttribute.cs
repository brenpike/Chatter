using System;
using Xunit;

namespace Chatter.Testing.Core.Integration
{
    // A [Theory] that runs only when Docker is reachable. When Docker is absent the theory is SKIPPED at
    // discovery time (Skip is set before the test runs) so a plain `dotnet test` on a Docker-free machine
    // reports its cases as skipped, never failed. The Category=Integration trait is applied at the test-class
    // level so CI can include/exclude with `--filter Category=Integration`. This is the [Theory] counterpart of
    // RequiresDockerFactAttribute and probes Docker exactly the same way; an integration fact that must run its
    // claim against more than one database setting takes this one.
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RequiresDockerTheoryAttribute : TheoryAttribute
    {
        public RequiresDockerTheoryAttribute()
        {
            if (!DockerEnvironment.IsAvailable)
            {
                Skip = DockerEnvironment.SkipReason;
            }
        }
    }
}
