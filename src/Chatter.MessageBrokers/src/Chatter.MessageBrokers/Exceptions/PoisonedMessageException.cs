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
    }
}
