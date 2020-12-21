using System.Threading;

namespace Chatter.CQRS.Context
{
    /// <summary>
    /// Context that is passed to <see cref="IMessageHandler{TMessage}"/> when an <see cref="IMessage"/> is handled.
    /// </summary>
    public interface IMessageHandlerContext : IContainContext
    {
        CancellationToken CancellationToken { get; }
    }
}
