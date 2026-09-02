using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics
{
    /// <summary>
    /// Serialises every diagnostics test in this assembly onto one xunit collection.
    /// </summary>
    /// <remarks>
    /// This is correctness, not tidiness. A .NET <c>ActivityListener</c> is PROCESS-GLOBAL and the Chatter
    /// source and meter names are fixed literals, so an opted-in test running concurrently with an absence
    /// test would let the absence test observe the opted-in test's .NET listener and fail intermittently.
    /// The definition MUST live in this test assembly: xunit v2 discovers collection definitions only in the
    /// assembly under run, which is why <c>Chatter.Testing.Core</c> deliberately declares none.
    /// </remarks>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class DiagnosticsCollection
    {
        /// <summary>The collection name every diagnostics test class is attributed with.</summary>
        public const string Name = "chatter-diagnostics";
    }
}
