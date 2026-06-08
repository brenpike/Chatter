using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// Global <see cref="JsonConverter{T}"/> for <see cref="object"/>-typed read positions. System.Text.Json
    /// deserializes an <c>object</c> member to a raw <see cref="JsonElement"/>; Newtonsoft's untyped read
    /// produced CLR-typed values (string/long/double/bool/DateTime, JObject/JArray). This converter restores
    /// that fidelity BY CONSTRUCTION at every object-typed position — message-context dictionaries,
    /// <c>RoutingSlip.Attachments</c> values, and any DTO member typed <c>object</c> — so no downstream typed
    /// reader observes a raw <see cref="JsonElement"/>, removing the need for per-seam materialization passes.
    /// </summary>
    /// <remarks>
    /// INVARIANT: Read routes through the single shared <see cref="MessageContext.MaterializeJsonElement"/>
    /// recipe (JsonElement-based, so ISO-8601 parse strictness and the per-value parity rules are identical
    /// to the persisted-context seams). Write is a NON-REENTRANT pass-through that serializes the value's
    /// RUNTIME type (never <c>typeof(object)</c>), which makes System.Text.Json resolve a different converter
    /// and therefore cannot re-enter this one — and produces byte-identical output to a plain runtime-typed
    /// serialize.
    /// </remarks>
    internal sealed class MaterializingObjectConverter : JsonConverter<object>
    {
        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return MessageContext.MaterializeJsonElement(document.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            var runtimeType = value.GetType();
            if (runtimeType == typeof(object))
            {
                // A bare System.Object instance serializes as an empty object; dispatching on its runtime
                // type would re-enter this converter, so emit the empty object directly.
                writer.WriteStartObject();
                writer.WriteEndObject();
                return;
            }

            // INVARIANT: serialize the RUNTIME type (not typeof(object)) so STJ resolves a different
            // converter — no self-reentry/stack-overflow — and emits byte-identical output to today.
            JsonSerializer.Serialize(writer, value, runtimeType, options);
        }
    }
}
