using System.Text.Json;

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
            // ChatterJsonEncoder is intentional: it preserves byte-for-byte wire compatibility with
            // the prior Newtonsoft.Json serialization, which emits +, <, >, &, ', /, and ALL
            // non-ASCII characters — including astral/supplementary-plane scalars such as emoji
            // (e.g. éü😀, U+1F600) — as their literal UTF-8 bytes rather than \uXXXX escapes.
            // UnsafeRelaxedJsonEscaping alone is NOT sufficient: it surrogate-escapes astral scalars
            // (😀 -> 😀), breaking parity. ChatterJsonEncoder decorates it to force all
            // non-ASCII literal while keeping its ASCII relaxation and structural escapes (", \, C0
            // controls). This is a cross-version interop contract: persisted outbox rows and rolling
            // deploys must round-trip identically across the Newtonsoft and STJ serializers. Parity
            // is gated by the WhenSerializingRiskyCharacters parity test.
            //
            // OUT OF CONTRACT: raw C0 control characters (U+0000–U+001F). The inner encoder
            // (UnsafeRelaxedJsonEscaping) emits JSON-standard short escapes (\n, \t, \r, etc.)
            // for the shortcut controls and \uXXXX for the remaining non-shortcut C0 scalars;
            // Newtonsoft's exact mapping differs for some of those non-shortcut scalars. Neither
            // form appears in real brokered-message content.
            // Ref: https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/character-encoding
            Encoder = ChatterJsonEncoder.Shared,

            // PascalCase property names (no NamingPolicy set) — matches Newtonsoft default.
            // WriteIndented = false (compact) — matches Newtonsoft default.

            // Newtonsoft's default object deserialization matches property names case-insensitively;
            // System.Text.Json defaults this to false. Without it, camelCase inbound bodies
            // (e.g. {"name":"abc","value":42}) silently deserialize to defaults, dropping data.
            // Read-path only — does NOT affect serialized output, so wire-output parity is preserved.
            PropertyNameCaseInsensitive = true,
        };
    }
}
