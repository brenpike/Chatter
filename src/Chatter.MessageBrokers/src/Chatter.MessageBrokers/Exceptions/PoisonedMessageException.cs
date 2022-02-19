using Chatter.MessageBrokers.Context;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class PoisonedMessageException : Exception
    {
        public PoisonedMessageException(string message)
            : base(message)
        { }

        public PoisonedMessageException(string message, Exception innerException)
            : base(message, innerException)
        { }

        public PoisonedMessageException(string message, Exception innerException, MessageBrokerContext context)
            : this(message, innerException)
        {
            Context = context;
        }

        /// <summary>
        /// Will be set if partially created <see cref="MessageBrokerContext"/> was able to be constructed but unable to be handled
        /// </summary>
        public MessageBrokerContext Context { get; private set; }
    }
}
