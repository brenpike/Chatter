using Chatter.CQRS.Context;

namespace Chatter.MessageBrokers.Routing.Options
{
    public interface IRoutingOptions : IContainContext
    {
        string MessageId { get; set; }
        string ContentType { get; set; }
    }
}
