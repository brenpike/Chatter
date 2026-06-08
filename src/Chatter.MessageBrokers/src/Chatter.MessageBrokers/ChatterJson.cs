using System.Text.Json;
using System.Text.Encodings.Web;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Shared System.Text.Json serializer options for all Chatter.MessageBrokers serialization sites.
    /// </summary>
    public static class ChatterJson
    {
        // INVARIANT: Options is constructed once and reused. STJ documents per-call construction
        // as a performance cliff; a single cached instance is the prescribed pattern.
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            // UnsafeRelaxedJsonEscaping is intentional: it preserves byte-for-byte wire
            // compatibility with the prior Newtonsoft.Json serialization, which emits +, <, >, &,
            // ', /, and non-ASCII/emoji characters (e.g. éü😀) as their literal UTF-8 bytes rather
            // than \uXXXX escape sequences. This is a cross-version interop contract: persisted
            // outbox rows and rolling deploys must round-trip identically across the Newtonsoft
            // and STJ serializers. Parity is gated by the WhenSerializingRiskyCharacters parity
            // test. Do not change this encoder without re-running the parity suite.
            // Ref: https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/character-encoding
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            // PascalCase property names (no NamingPolicy set) — matches Newtonsoft default.
            // WriteIndented = false (compact) — matches Newtonsoft default.
        };
    }
}
