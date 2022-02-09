using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    class RetryWithCircuitBreakerStrategy : IRecoveryStrategy
    {
        private readonly RecoveryOptions _options;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly IRetryStrategy _retry;

        public RetryWithCircuitBreakerStrategy(RecoveryOptions options, ICircuitBreaker circuitBreaker, IRetryStrategy retry)
        {
            _options = options;
            _circuitBreaker = circuitBreaker;
            _retry = retry;
        }

        public Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken token)
        {
            return _retry.ExecuteAsync(() =>
            {
                return _circuitBreaker.ExecuteAsync(_ =>
                {
                    return action();
                }, token);
            }, token);
        }
    }
}
