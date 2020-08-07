using System.Text.Json;
using System.Text;

namespace Chatter.MessageBrokers
{
    public class JsonBodyConverter : IBrokeredMessageBodyConverter
    {
        public string ContentType => "application/json";

        public TBody Convert<TBody>(byte[] body)
        {
            return JsonSerializer.Deserialize<TBody>(Stringify(body));
        }

        public byte[] Convert(object body)
        {
            return Encoding.UTF8.GetBytes(Stringify(body));
        }

        public string Stringify(byte[] body)
        {
            return Encoding.UTF8.GetString(body);
        }

        public string Stringify(object body)
        {
            return JsonSerializer.Serialize(body);
        }
    }
}
