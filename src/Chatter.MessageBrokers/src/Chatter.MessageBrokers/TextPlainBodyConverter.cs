using System;
using System.Text;

namespace Chatter.MessageBrokers
{
    public class TextBodyConverter : IBrokeredMessageBodyConverter
    {
        private const string _stronglyTypedConversionFailureMessage = "A strongly typed body is required. Consider using a content type like application/json.";
        private readonly JsonBodyConverter _jsonBodyConverter;

        public TextBodyConverter()
        {
            _jsonBodyConverter = new JsonBodyConverter();
        }

        //TODO: This implementation is temporary. Change this impl. to some sort of successor pattern
        //      where it tries json, then xml, etc. and then throws an exception otherwise.
        //      Chatter requires a strongly typed object to function correctly, so plain text won't 
        //      work.  However, libs like Azure Service Bus default content type to text/plain, so 
        //      this impl. will help if users of those libraries are sloppy and don't set content type
        //      appropriately to json or xml, etc.

        public string ContentType => "text/plain";

        public TBody Convert<TBody>(byte[] body)
        {
            try
            {
                return _jsonBodyConverter.Convert<TBody>(body);
            }
            catch (Exception e)
            {
                throw new Exception(_stronglyTypedConversionFailureMessage, e);
            }
        }

        public byte[] Convert(object body)
            => GetBytes(Stringify(body));

        public string Stringify(byte[] body)
            => Encoding.UTF8.GetString(body);

        public string Stringify(object body)
        {
            try
            {
                return _jsonBodyConverter.Stringify(body);
            }
            catch (Exception e)
            {
                throw new Exception(_stronglyTypedConversionFailureMessage, e);
            }
        }

        public byte[] GetBytes(string body)
            => Encoding.UTF8.GetBytes(body);
    }
}
