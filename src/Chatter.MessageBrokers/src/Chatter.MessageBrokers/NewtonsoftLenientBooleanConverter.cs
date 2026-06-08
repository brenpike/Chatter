using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Global boolean <see cref="JsonConverterFactory"/> restoring Newtonsoft read-leniency parity for
    /// <see cref="bool"/> and <see cref="Nullable{Boolean}"/> DTO properties: Newtonsoft's default
    /// <c>JsonConvert.DeserializeObject</c> coerced a QUOTED boolean (e.g. <c>{"Enabled":"true"}</c>) into a
    /// <c>bool</c> member, whereas System.Text.Json's shared <see cref="ChatterJson.Options"/> only restores
    /// quoted NUMERIC reads (<see cref="JsonNumberHandling.AllowReadingFromString"/> covers numbers, NOT
    /// bools) and throws <see cref="JsonException"/> on a quoted boolean. Both <see cref="JsonBodyConverter"/>
    /// and the SSB <c>JsonUnicodeBodyConverter</c> read through the shared options, so such messages now throw
    /// after the migration. This converter accepts a quoted boolean on READ while WRITING the real JSON
    /// boolean token, so the wire output stays byte-identical to both the prior Newtonsoft serialization and
    /// the pre-converter STJ output (both write the bare <c>true</c>/<c>false</c> token, never a quoted form).
    /// Golden byte-parity is therefore preserved.
    /// </summary>
    /// <remarks>
    /// READ-leniency-only by construction: the <see cref="JsonConverter{T}.Write"/> path emits the real
    /// boolean via <see cref="Utf8JsonWriter.WriteBooleanValue(bool)"/>, never a quoted string, so no wire
    /// change is introduced.
    /// <para>
    /// Accepted quoted forms (matching the common Newtonsoft cases): <c>"true"</c>/<c>"false"</c>
    /// case-insensitively (<see cref="bool.TryParse(string, out bool)"/>) and the integer-string forms
    /// <c>"1"</c>/<c>"0"</c>. A bare JSON number <c>1</c>/<c>0</c> is also coerced (Newtonsoft tolerated
    /// integer-to-bool). Any other string throws <see cref="JsonException"/>, identical to a genuinely
    /// invalid value.
    /// </para>
    /// <para>
    /// This factory ONLY fires for strongly-typed <c>bool</c>/<c>bool?</c> members. A boolean at an
    /// <c>object</c>-typed read position is handled by <see cref="MaterializingObjectConverter"/> (which
    /// intercepts <c>typeof(object)</c> only), so a JSON <c>true</c>/<c>false</c> at an object position
    /// materializes to a CLR <c>bool</c> while a QUOTED <c>"true"</c> at an object position stays a
    /// <c>string</c> — matching Newtonsoft's untyped read. This converter does NOT touch object positions,
    /// so that distinction is preserved.
    /// </para>
    /// </remarks>
    internal sealed class NewtonsoftLenientBooleanConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert == typeof(bool) || typeToConvert == typeof(bool?);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => typeToConvert == typeof(bool?)
                ? new NullableBooleanConverter()
                : new BooleanConverter();

        // Parse a quoted boolean the way Newtonsoft's default read did: "true"/"false" case-insensitively,
        // plus the integer-string forms "1"/"0". Throws JsonException on any other string.
        private static bool ParseQuotedBoolean(string value)
        {
            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            if (value == "1")
            {
                return true;
            }

            if (value == "0")
            {
                return false;
            }

            throw new JsonException(
                $"The JSON value '{value}' could not be converted to System.Boolean.");
        }

        private static bool ReadBoolean(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;

                case JsonTokenType.False:
                    return false;

                case JsonTokenType.String:
                    return ParseQuotedBoolean(reader.GetString());

                case JsonTokenType.Number:
                    // Newtonsoft tolerated an integer 1/0 for a bool member.
                    var number = reader.GetInt64();
                    if (number == 1)
                    {
                        return true;
                    }

                    if (number == 0)
                    {
                        return false;
                    }

                    throw new JsonException(
                        $"The JSON value '{number}' could not be converted to System.Boolean.");

                default:
                    throw new JsonException(
                        $"Unexpected token {reader.TokenType} when reading System.Boolean.");
            }
        }

        private sealed class BooleanConverter : JsonConverter<bool>
        {
            public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => ReadBoolean(ref reader);

            // Write the real JSON boolean token — byte-identical to Newtonsoft and the STJ default.
            public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
                => writer.WriteBooleanValue(value);
        }

        private sealed class NullableBooleanConverter : JsonConverter<bool?>
        {
            public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null)
                {
                    return null;
                }

                return ReadBoolean(ref reader);
            }

            public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
            {
                if (value is null)
                {
                    writer.WriteNullValue();
                    return;
                }

                writer.WriteBooleanValue(value.Value);
            }
        }
    }
}
