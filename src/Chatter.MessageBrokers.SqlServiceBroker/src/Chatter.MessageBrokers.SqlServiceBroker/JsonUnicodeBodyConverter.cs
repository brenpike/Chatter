using System.Text;
using System.Text.Json;

namespace Chatter.MessageBrokers.SqlServiceBroker
{
    public class JsonUnicodeBodyConverter : IBrokeredMessageBodyConverter
    {
        public string ContentType => "application/json; charset=utf-16";

        public TBody Convert<TBody>(byte[] body)
            => JsonSerializer.Deserialize<TBody>(Stringify(body), ChatterJson.Options);

        public byte[] Convert(object body)
            => GetBytes(Stringify(body));

        public string Stringify(byte[] body)
            => Encoding.Unicode.GetString(body);

        public string Stringify(object body)
            => JsonSerializer.Serialize(body, ChatterJson.Options);

        public byte[] GetBytes(string body)
            => Encoding.Unicode.GetBytes(body);
    }
}
