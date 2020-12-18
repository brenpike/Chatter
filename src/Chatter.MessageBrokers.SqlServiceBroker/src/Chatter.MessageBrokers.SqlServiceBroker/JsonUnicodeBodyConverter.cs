using Newtonsoft.Json;
using System.Text;

namespace Chatter.MessageBrokers.SqlServiceBroker
{
    public class JsonUnicodeBodyConverter : IBrokeredMessageBodyConverter
    {
        public string ContentType => "application/json; charset=utf-16";

        public TBody Convert<TBody>(byte[] body)
            => JsonConvert.DeserializeObject<TBody>(Stringify(body));

        public byte[] Convert(object body)
            => GetBytes(Stringify(body));

        public string Stringify(byte[] body) 
            => Encoding.Unicode.GetString(body);

        public string Stringify(object body)
            => JsonConvert.SerializeObject(body);

        public byte[] GetBytes(string body)
            => Encoding.Unicode.GetBytes(body);
    }
}
