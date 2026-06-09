using System;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // A [Fact] that runs only when a real Azure Service Bus namespace connection string is configured (see
    // RealNamespaceEnvironment). When it is absent the fact is SKIPPED at discovery time (Skip is set before
    // the test runs) so a plain `dotnet test` reports these as skipped, never failed. The
    // Category=RealNamespaceIntegration trait is applied at the test-class level (see
    // RealNamespaceCrossEntityTransactionTests) — deliberately WITHOUT the Integration trait so the emulator
    // CI lane (`--filter Category=Integration`) does not re-select these cross-entity tests.
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RequiresRealServiceBusNamespaceFactAttribute : FactAttribute
    {
        public RequiresRealServiceBusNamespaceFactAttribute()
        {
            if (!RealNamespaceEnvironment.IsConfigured)
            {
                Skip = RealNamespaceEnvironment.SkipReason;
            }
        }
    }
}
