namespace Chatter.CQRS
{
    /// <summary>
    /// Creates an <see cref="IMessageDispatcher"/>
    /// </summary>
    public interface IMessageDispatcherFactory
    {
        /// <summary>
        /// Creates an <see cref="IMessageDispatcher"/> based on the <typeparamref name="TMessage"/> provided.
        /// </summary>
        /// <typeparam name="TMessage">The type of <see cref="IMessageDispatcher"/> to create.</typeparam>
        /// <returns>A message dispatcher</returns>
        IMessageDispatcher CreateDispatcher<TMessage>() where TMessage : IMessage;
    }
}
