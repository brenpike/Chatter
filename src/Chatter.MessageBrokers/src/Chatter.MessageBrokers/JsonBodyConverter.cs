using Newtonsoft.Json;
using System.Text;

namespace Chatter.MessageBrokers
{
    public class JsonBodyConverter : IBrokeredMessageBodyConverter
    {
        public string ContentType => "application/json";

        public TBody Convert<TBody>(byte[] body)
        {
            return JsonConvert.DeserializeObject<TBody>(Encoding.UTF8.GetString(body));
        }

        public byte[] Convert(object body)
        {
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
        }
    }
}
