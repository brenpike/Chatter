using Newtonsoft.Json;
using System.Text;

namespace Chatter.MessageBrokers
{
    public class JsonBodyConverter : IBrokeredMessageBodyConverter
    {
        public string ContentType => "application/json";

        public TBody Convert<TBody>(byte[] body)
            => JsonConvert.DeserializeObject<TBody>(Stringify(body));

        public byte[] Convert(object body)
            => Encoding.UTF8.GetBytes(Stringify(body));

        public string Stringify(byte[] body)
            => Encoding.UTF8.GetString(body);

        public string Stringify(object body)
            => JsonConvert.SerializeObject(body);

        public byte[] GetBytes(string body)
            => Encoding.UTF8.GetBytes(body);
    }
}
