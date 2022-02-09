using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class CriticalReceiverException : Exception
    {
        public CriticalReceiverException(Exception inner)
            : base("Critical error received, unable to receive messages", inner)
        { }
    }
}
