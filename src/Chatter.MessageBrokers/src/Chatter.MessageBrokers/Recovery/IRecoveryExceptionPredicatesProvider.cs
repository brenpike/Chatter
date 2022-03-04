using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Recovery
{
    public interface IRecoveryExceptionPredicatesProvider
    {
        IEnumerable<Predicate<Exception>> GetExceptionPredicates();
    }
}
