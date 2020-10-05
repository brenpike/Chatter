using Chatter.MessageBrokers.Context;
using System;

namespace Chatter.MessageBrokers.Exceptions
{
    public class CriticalBrokeredMessageReceiverException : Exception
    {
        public FailureContext ErrorContext { get; }

        public CriticalBrokeredMessageReceiverException(FailureContext errorContext, Exception innerException)
            : base(errorContext.ErrorDetails, innerException)
        {
            ErrorContext = errorContext;
        }
    }
}
