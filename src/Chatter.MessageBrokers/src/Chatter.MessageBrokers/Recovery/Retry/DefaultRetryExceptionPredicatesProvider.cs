using Chatter.MessageBrokers.Exceptions;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    /// <summary>
    /// Classifies a receive failure as retryable by EXCEPTION TYPE only. A transient
    /// <see cref="BrokeredMessageReceiverException"/> is retried; nothing else is. Message TEXT is never
    /// inspected, because a handler exception routinely echoes message-body content, so a crafted or unlucky
    /// message would otherwise be classified transient and retried on everyone's behalf.
    /// </summary>
    internal sealed class DefaultRetryExceptionPredicatesProvider : IRetryExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
            yield return new Predicate<Exception>(e => e is BrokeredMessageReceiverException exception && exception.IsTransient);
        }
    }
}
