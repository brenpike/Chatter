using System;

namespace Chatter.CQRS
{
    public interface IMessageDispatcherProvider
    {
        /// <summary>
        /// The type of message for which a message dispatcher must be provided
        /// </summary>
        Type DispatchType { get; }
        /// <summary>
        /// Gets an <see cref="IMessageDispatcher"/>
        /// </summary>
        /// <returns>The message dispatcher for type <see cref="DispatchType"/></returns>
        IMessageDispatcher GetDispatcher();
    }
}
