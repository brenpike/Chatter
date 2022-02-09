using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving.CircuitBreaker
{
    public class SqlCircuitBreakerExceptionPredicatesProvider : ICircuitBreakerExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
#if NET5_0_OR_GREATER
            yield return new Predicate<Exception>(e => e.GetType() == typeof(SqlException) && ((SqlException)e).IsTransient);
#endif
            yield return new Predicate<Exception>(e => e.GetType() == typeof(SqlException) && SqlExceptionHelper.IsErrorNumberTransient(((SqlException)e).Number));
        }
    }
}
