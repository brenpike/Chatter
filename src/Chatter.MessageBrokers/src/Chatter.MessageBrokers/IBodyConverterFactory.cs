namespace Chatter.MessageBrokers
{
    public interface IBodyConverterFactory
    {
        IBrokeredMessageBodyConverter CreateBodyConverter(string contentType);
    }
}
