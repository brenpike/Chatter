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

            // Newtonsoft's JsonConvert serializes/deserializes public FIELDS by default;
            // System.Text.Json defaults IncludeFields to false, so a contract exposing public
            // fields (e.g. class Event { public string Name; }) would serialize as {} and
            // deserialize to defaults, silently dropping data. Enabling this restores
            // Newtonsoft-compatible field handling on BOTH read and write paths. Public fields
            // serialize by their PascalCase declared name (no NamingPolicy set), so wire-output
            // parity with the prior Newtonsoft serialization is preserved.
            IncludeFields = true,

            // ---- Newtonsoft read-leniency parity ----
            // The following three options restore deserialize-side leniencies that Newtonsoft's
            // default JsonConvert tolerated but System.Text.Json tightened to throw. They are all
            // READ-path only and MUST NOT change serialized wire bytes (golden byte-parity tests
            // stay byte-identical); they exist so existing producers' messages keep deserializing.

            // Newtonsoft coerced quoted numeric values (e.g. {"RetryCount":"3"}) into int/long DTO
            // properties; STJ defaults strict and throws JsonException. AllowReadingFromString is
            // READ-only — it does NOT serialize numbers as strings (no WriteAsString), so wire
            // output is unchanged.
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,

            // Newtonsoft tolerated trailing commas (e.g. {"Name":"abc",}); STJ defaults false and
            // throws. Read-path only; serialize never emits trailing commas.
            AllowTrailingCommas = true,

            // Newtonsoft tolerated // line and /* block */ comments in inbound JSON; STJ defaults
            // Disallow and throws. Skip ignores them on read; serialize never emits comments.
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        };
    }
}
