using System.Text;
using System.Text.Json;

namespace Chatter.MessageBrokers.RabbitMQ
{
    public class RabbitMqBodyConverter : IBrokeredMessageBodyConverter
    {
        public string ContentType => "application/json; charset=utf-8";

        public TBody Convert<TBody>(byte[] body)
            => JsonSerializer.Deserialize<TBody>(Stringify(body), ChatterJson.Options);

        public byte[] Convert(object body)
            => GetBytes(Stringify(body));

        public string Stringify(byte[] body)
            => Encoding.UTF8.GetString(body);

        public string Stringify(object body)
            => JsonSerializer.Serialize(body, ChatterJson.Options);

        public byte[] GetBytes(string body)
            => Encoding.UTF8.GetBytes(body);
    }
}
