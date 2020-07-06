namespace Chatter.MessageBrokers
{
    public interface IBrokeredMessageBodyConverter
    {
        string ContentType { get; }
        TBody Convert<TBody>(byte[] body);
        byte[] Convert(object body);
    }
}
