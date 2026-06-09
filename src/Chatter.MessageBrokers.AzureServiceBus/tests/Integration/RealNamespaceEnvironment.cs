using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Discovery-time gate for the real-namespace cross-entity transaction tests. The Azure Service Bus
    // emulator cannot exercise cross-entity (multi-top-level-entity) transactions — it throws "Local
    // transactions cannot span multiple top-level entities" — so those tests must run against a real
    // Azure Service Bus namespace. The connection string is supplied out-of-band via the
    // CHATTER_ASB_REAL_NAMESPACE_CONNECTION_STRING environment variable; when it is absent the
    // Category=RealNamespaceIntegration tests are SKIPPED (never failed) so a plain `dotnet test` stays
    // green. Mirrors DockerEnvironment.
    internal static class RealNamespaceEnvironment
    {
        public const string ConnectionStringVariable = "CHATTER_ASB_REAL_NAMESPACE_CONNECTION_STRING";

        public const string SkipReason =
            "No real Azure Service Bus namespace configured. Set the " +
            "CHATTER_ASB_REAL_NAMESPACE_CONNECTION_STRING environment variable to a connection string with " +
            "the Manage claim to execute the Category=RealNamespaceIntegration cross-entity transaction tests.";

        // The configured connection string, or null when the environment variable is unset/blank.
        public static string ConnectionString
            => Environment.GetEnvironmentVariable(ConnectionStringVariable);

        // True only when a non-whitespace connection string is configured.
        public static bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
    }
}
