using System;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace Chatter.MessageBrokers
{
    public class JsonBodyConverter : IBrokeredMessageBodyConverter
    {
        public string ContentType => "application/json";

        public TBody Convert<TBody>(byte[] body)
        {
            ArgumentNullException.ThrowIfNull(body);

            return Utf8.IsValid(body)
                ? JsonSerializer.Deserialize<TBody>(body.AsSpan(), ChatterJson.Options)
                : JsonSerializer.Deserialize<TBody>(Stringify(body), ChatterJson.Options);
        }

        public byte[] Convert(object body)
            => GetBytes(Stringify(body));

        public string Stringify(byte[] body)
            => Encoding.UTF8.GetString(body);

        public string Stringify(object body)
            => body is null
                ? JsonSerializer.Serialize<object>(null, ChatterJson.Options)
                : JsonSerializer.Serialize(body, body.GetType(), ChatterJson.Options);

        public byte[] GetBytes(string body)
            => Encoding.UTF8.GetBytes(body);
    }
}
