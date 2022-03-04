using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class BrokeredMessageReceiverException : Exception
    {
        public bool IsTransient { get; }

        public BrokeredMessageReceiverException(string message, bool isTransient)
            : base(message)
        {
            IsTransient = isTransient;
        }

        public BrokeredMessageReceiverException(string message, Exception inner, bool isTransient)
            : base(message, inner)
        {
            IsTransient = isTransient;
        }
    }
}
