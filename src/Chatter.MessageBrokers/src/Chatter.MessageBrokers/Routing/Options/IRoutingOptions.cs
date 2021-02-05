namespace Chatter.MessageBrokers.Routing.Options
{
    public interface IRoutingOptions
    {
        string MessageId { get; set; }
        string ContentType { get; set; }
    }
}
