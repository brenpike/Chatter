using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Global enum <see cref="JsonConverterFactory"/> restoring Newtonsoft read-leniency parity for enum DTO
    /// properties: Newtonsoft's default <c>JsonConvert.DeserializeObject</c> accepted BOTH the enum NAME
    /// (e.g. <c>{"Status":"Booked"}</c>) and its numeric value on read, whereas System.Text.Json's shared
    /// <see cref="ChatterJson.Options"/> (no enum converter) reads numbers ONLY and throws
    /// <see cref="JsonException"/> on a name. This converter accepts names (case-insensitively, matching
    /// Newtonsoft) AND numbers on READ, while WRITING the numeric value so the wire output stays byte-identical
    /// to both the prior Newtonsoft serialization and the pre-converter STJ output (Newtonsoft's default and
    /// STJ's default both write enums as numbers). Golden byte-parity is therefore preserved.
    /// </summary>
    /// <remarks>
    /// READ-leniency-only by construction: the <see cref="JsonConverter{T}.Write"/> path emits the numeric
    /// value via <see cref="Utf8JsonWriter.WriteNumberValue(long)"/> (signed) / <c>WriteNumberValue(ulong)</c>
    /// (for <c>ulong</c>-backed enums), never the name, so no <c>WriteAsString</c>-style wire change is
    /// introduced. Nullable enum members are handled by STJ's surrounding nullable wrapper (it unwraps to the
    /// underlying enum type and dispatches here), so this factory only needs to match non-nullable enum types.
    /// </remarks>
    internal sealed class NumericWriteStringReadEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var converterType = typeof(EnumConverter<>).MakeGenericType(typeToConvert);
            return (JsonConverter)Activator.CreateInstance(converterType);
        }

        private sealed class EnumConverter<TEnum> : JsonConverter<TEnum>
            where TEnum : struct, Enum
        {
            private static readonly Type UnderlyingType = Enum.GetUnderlyingType(typeof(TEnum));

            public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        // Newtonsoft parsed enum names case-insensitively (and tolerated comma-separated
                        // [Flags] combinations). Enum.Parse(ignoreCase: true) restores both behaviors.
                        var name = reader.GetString();
                        if (Enum.TryParse<TEnum>(name, ignoreCase: true, out var parsed))
                        {
                            return parsed;
                        }

                        throw new JsonException(
                            $"The JSON value '{name}' could not be converted to {typeof(TEnum)}.");

                    case JsonTokenType.Number:
                        // STJ's own numeric read path. Route through the underlying integral type so values
                        // outside the int range (long/ulong-backed enums) round-trip.
                        return ReadNumber(ref reader);

                    default:
                        throw new JsonException(
                            $"Unexpected token {reader.TokenType} when reading enum {typeof(TEnum)}.");
                }
            }

            public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            {
                // Write the NUMERIC value to preserve Newtonsoft/STJ default numeric wire parity.
                if (UnderlyingType == typeof(ulong))
                {
                    writer.WriteNumberValue((ulong)Convert.ToUInt64(value));
                }
                else
                {
                    writer.WriteNumberValue(Convert.ToInt64(value));
                }
            }

            private static TEnum ReadNumber(ref Utf8JsonReader reader)
            {
                if (UnderlyingType == typeof(ulong))
                {
                    return (TEnum)Enum.ToObject(typeof(TEnum), reader.GetUInt64());
                }

                return (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt64());
            }
        }
    }
}
