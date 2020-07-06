using Chatter.MessageBrokers.Context;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class CriticalBrokeredMessageReceiverException : Exception
    {
        public ErrorContext ErrorContext { get; }

        public CriticalBrokeredMessageReceiverException(ErrorContext errorContext, Exception innerException)
            : base(errorContext.ErrorDetails, innerException)
        {
            ErrorContext = errorContext;
        }
    }
}
