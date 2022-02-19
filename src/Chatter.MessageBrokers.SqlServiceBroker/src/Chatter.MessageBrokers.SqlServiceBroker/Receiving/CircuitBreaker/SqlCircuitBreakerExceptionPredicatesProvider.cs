﻿using Chatter.MessageBrokers.Recovery.CircuitBreaker;
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
            yield return new Predicate<Exception>(e => e is SqlException exception && exception.IsTransient);
#endif
            yield return new Predicate<Exception>(e => e is SqlException exception && SqlExceptionHelper.IsErrorNumberTransient(exception.Number));
        }
    }
}