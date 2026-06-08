using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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

            // Newtonsoft populated EXISTING initialized getter-only collections/objects through
            // the getter — a contract exposing `public List<Item> Items { get; } = new();` had its
            // inbound array values added INTO the already-constructed instance. System.Text.Json
            // defaults object-creation handling to Replace and (since the member has no setter)
            // the EnableNonPublicSetters modifier skips it, so such DTOs arrive with an EMPTY
            // collection, silently dropping data on both JsonBodyConverter and JsonUnicodeBodyConverter.
            // Populate makes STJ add into the existing initialized instance instead of replacing it,
            // restoring Newtonsoft's behavior. DESERIALIZE-side only — it does NOT change serialized
            // wire bytes, so golden byte-parity tests stay byte-identical. .NET 8+ API (net8.0 + net10.0).
            PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate,

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

            // Newtonsoft populated NON-PUBLIC property setters (private/protected/internal) by
            // default — common for immutable command/event DTOs declared as
            // `public string Name { get; private set; }`. System.Text.Json only binds PUBLIC
            // setters or constructor parameters, so such contracts now silently deserialize to
            // defaults (or fail) on the shared read path. EnableNonPublicSetters restores the
            // Newtonsoft default via a contract-model modifier (see below). DESERIALIZE-side only
            // (wires JsonPropertyInfo.Set, never Get), so serialized wire bytes are unchanged and
            // golden byte-parity tests stay byte-identical.
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { EnableNonPublicSetters }
            },

            // MaterializingObjectConverter restores CLR-type fidelity at every object-typed READ position
            // (message-context values, RoutingSlip.Attachments values, any DTO `object` member): STJ would
            // otherwise surface a raw JsonElement, whereas Newtonsoft's untyped read produced string/long/
            // double/bool/DateTime and nested Dictionary<string,object>/List<object>. It only intercepts
            // typeToConvert == typeof(object) (RoutingSlip.Route/Visited are IList<RoutingStep> — typed, NOT
            // object — so untouched). Its Write path is a non-reentrant runtime-typed pass-through, so wire
            // output stays byte-identical (golden parity preserved).
            //
            // NumericWriteStringReadEnumConverter restores Newtonsoft read-leniency for enum DTO properties:
            // Newtonsoft's default read accepted BOTH the enum NAME (e.g. {"Status":"Booked"}) and its number,
            // whereas STJ (with no enum converter) reads numbers ONLY and throws on a name. This factory reads
            // names (case-insensitively) AND numbers, while WRITING the numeric value — Newtonsoft's default
            // and STJ's default both write enums as numbers — so wire-output byte parity is preserved.
            Converters =
            {
                new MaterializingObjectConverter(),
                new NumericWriteStringReadEnumConverter(),
            },
        };

        // Contract-model modifier: for any property STJ left unsettable on deserialize
        // (Set == null — i.e. no public setter and no [JsonInclude]-forced setter) whose
        // underlying CLR member nonetheless exposes a NON-PUBLIC setter, wire Set to invoke
        // that setter via reflection. This restores Newtonsoft's default private-setter binding
        // globally for ALL body/context deserialization through ChatterJson.Options.
        //
        // No-op (and MUST stay a no-op) for:
        //   - properties with a public setter — STJ already set Set (skipped: Set != null)
        //   - [JsonInclude] members (e.g. RoutingSlip.Route/Attachments/Visited with internal/
        //     private setters) — STJ already set Set for them (skipped: Set != null)
        //   - constructor-parameter-bound members on [JsonConstructor] types (RoutingSlip,
        //     RoutingStep, OutboundBrokeredMessage) and records — these expose NO setter
        //     (GetSetMethod(nonPublic: true) == null), so ctor binding is untouched
        //   - fields (IncludeFields) — AttributeProvider is a FieldInfo, not PropertyInfo
        private static void EnableNonPublicSetters(JsonTypeInfo typeInfo)
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object)
            {
                return;
            }

            foreach (var property in typeInfo.Properties)
            {
                if (property.Set is not null)
                {
                    // STJ already found a bindable setter (public or [JsonInclude]-forced).
                    continue;
                }

                if (property.AttributeProvider is not PropertyInfo propertyInfo)
                {
                    // Fields and synthesized members are not non-public-setter properties.
                    continue;
                }

                var setMethod = propertyInfo.GetSetMethod(nonPublic: true);
                if (setMethod is null)
                {
                    // No CLR setter at all (get-only / ctor-bound) — leave to ctor binding.
                    continue;
                }

                property.Set = (obj, value) => propertyInfo.SetValue(obj, value);
            }
        }
    }
}
