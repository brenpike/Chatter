using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class ReceiverMessageDispatchingException : Exception
    {
        public ReceiverMessageDispatchingException(string message, Exception innerException) 
            : base(message, innerException)
        {
        }
    }
}
