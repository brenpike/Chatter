using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class CriticalReceiverException : Exception
    {
        public CriticalReceiverException(string message)
            : base(message) { }

        public CriticalReceiverException(Exception inner)
            : base("Critical error received, unable to receive messages", inner) { }

        public CriticalReceiverException(string message, Exception inner)
            : base(message, inner) { }
    }
}
