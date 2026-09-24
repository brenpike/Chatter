using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving.CircuitBreaker
{
    internal sealed class SqlCircuitBreakerExceptionPredicatesProvider : ICircuitBreakerExceptionPredicatesProvider
    {
        public IEnumerable<Predicate<Exception>> GetExceptionPredicates()
        {
            // Microsoft.Data.SqlClient owns IsTransient and may report a terminal error number as
            // transient, so the terminal set overrides it. No unit test pins this guard: SqlException
            // cannot be constructed without a live SQL connection (see the CHARACTERIZATION BOUNDARY
            // in WhenGettingExceptionPredicates).
            yield return new Predicate<Exception>(e => e is SqlException exception && exception.IsTransient && !SqlExceptionHelper.IsErrorNumberTerminal(exception.Number));
            // No terminal guard here: this package owns IsErrorNumberTransient, and it is pinned disjoint
            // from the terminal set by WhenCheckingErrorNumberTerminality.MustNeverClassifyATerminalErrorNumberAsTransient.
            yield return new Predicate<Exception>(e => e is SqlException exception && SqlExceptionHelper.IsErrorNumberTransient(exception.Number));
        }
    }
}
