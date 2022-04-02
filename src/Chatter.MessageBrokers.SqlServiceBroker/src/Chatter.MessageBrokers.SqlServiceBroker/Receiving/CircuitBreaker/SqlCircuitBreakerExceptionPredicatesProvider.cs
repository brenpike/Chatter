using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving.CircuitBreaker
{
    internal sealed class SqlCircuitBreakerExceptionPredicatesProvider : ICircuitBreakerExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
#if NET5_0_OR_GREATER
            yield return new Predicate<Exception>(e => e is SqlException exception && exception.IsTransient);
#endif
            yield return new Predicate<Exception>(e => e is SqlException exception && SqlExceptionHelper.IsErrorNumberTransient(exception.Number));
            yield return new Predicate<Exception>(e => e is SqlException exception && exception.Number == 208); //invalid object name
        }
    }
}
