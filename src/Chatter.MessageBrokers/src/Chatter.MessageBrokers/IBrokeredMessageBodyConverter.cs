using System;

namespace Chatter.MessageBrokers
{
    public interface IBrokeredMessageBodyConverter
    {
        string ContentType { get; }
        TBody Convert<TBody>(byte[] body);
        byte[] Convert(object body);
        string Stringify(byte[] body);
        string Stringify(object body);
    }
}
