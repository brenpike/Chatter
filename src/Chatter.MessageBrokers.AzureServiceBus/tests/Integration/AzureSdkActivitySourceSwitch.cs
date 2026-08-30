using System;
using System.Runtime.CompilerServices;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Turns ON Azure.Messaging.ServiceBus 7.20.1's own ActivitySource tracing for THIS test process, so
    // AzureServiceBusTraceContextInteropTests can observe how the SDK's instrumentation and Chatter's
    // instrumentation actually interact instead of asserting against an SDK that never emitted anything.
    //
    // WHY A MODULE INITIALIZER RATHER THAN TEST SETUP. The SDK reads the switch ONCE, from a static
    // constructor, and caches the answer for the life of the process. Verified against the resolved package
    // rather than assumed: decompiling Azure.Messaging.ServiceBus 7.20.1 (which compiles its OWN internal copy
    // of the Azure.Core shared source, so this type also exists in Azure.Core 1.46.2) shows
    //
    //     internal static class ActivityExtensions                    // Azure.Core.Pipeline
    //     {
    //         public static bool SupportsActivitySource { get; private set; }
    //         static ActivityExtensions() => ResetFeatureSwitch();
    //         public static void ResetFeatureSwitch()
    //             => SupportsActivitySource = AppContextSwitchHelper.GetConfigValue(
    //                    "Azure.Experimental.EnableActivitySource",
    //                    "AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE");
    //     }
    //
    // and AppContextSwitchHelper.GetConfigValue prefers AppContext.TryGetSwitch, falling back to the
    // environment variable when the switch was never set. DiagnosticScopeFactory.GetActivitySource then
    // returns null unless SupportsActivitySource is true, so the SDK creates no ActivitySource at all when the
    // switch was read as false. Setting the switch AFTER anything has touched ActivityExtensions is therefore
    // silently too late: the type is already initialized and the cached false stands. A [ModuleInitializer]
    // runs before any other code in this test assembly executes, and the SDK is only ever reached FROM this
    // assembly, so the switch is guaranteed to be in place before the first touch of the SDK type.
    //
    // CI-LANE EQUIVALENT: exporting AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true (accepted values "true",
    // case-insensitively, or "1") before the test run has the same effect, and is the form an application or a
    // pipeline uses. The AppContext switch is used here instead so the behaviour does not depend on how the
    // test host was launched.
    //
    // BLAST RADIUS. AppContext switches are process-wide, and the process is this one test assembly's runner.
    // The switch only decides whether the SDK is WILLING to create its ActivitySources; it emits nothing
    // unless a .NET System.Diagnostics.ActivityListener (the BCL subscription type -- never a Brokered Message
    // Receiver) subscribes to one of them, and only the interop tests subscribe. It writes no environment
    // variable and touches no other process state.
    internal static class AzureSdkActivitySourceSwitch
    {
        // The AppContext switch Azure.Core's AppContextSwitchHelper reads first.
        public const string SwitchName = "Azure.Experimental.EnableActivitySource";

        // The environment variable Azure.Core's AppContextSwitchHelper falls back to; the CI-lane equivalent.
        public const string EnvironmentVariableName = "AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE";

        // Whether the switch is on as this process sees it, resolved the same way the SDK resolves it. The
        // interop tests assert on this so a switch that failed to take effect fails loudly rather than
        // producing a matrix that proves nothing.
        public static bool IsEnabled
        {
            get
            {
                if (AppContext.TryGetSwitch(SwitchName, out var isEnabled))
                {
                    return isEnabled;
                }

                var environmentValue = Environment.GetEnvironmentVariable(EnvironmentVariableName);
                return string.Equals(environmentValue, "true", StringComparison.OrdinalIgnoreCase) || environmentValue == "1";
            }
        }

        [ModuleInitializer]
        internal static void EnableAzureSdkActivitySource() => AppContext.SetSwitch(SwitchName, true);
    }
}
