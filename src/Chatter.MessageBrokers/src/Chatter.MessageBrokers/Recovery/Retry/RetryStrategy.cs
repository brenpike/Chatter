using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    class RetryStrategy : IRetryStrategy
    {
        private readonly RecoveryOptions _options;
        private readonly IRetryDelayStrategy _delayedRecovery;
        private readonly IRetryExceptionEvaluator _exceptionEvaluator;
        private readonly ILogger<RetryStrategy> _logger;

        public RetryStrategy(RecoveryOptions options, ILogger<RetryStrategy> logger, IRetryDelayStrategy delayedRecovery, IRetryExceptionEvaluator exceptionEvaluator)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _delayedRecovery = delayedRecovery ?? throw new ArgumentNullException(nameof(delayedRecovery));
            _exceptionEvaluator = exceptionEvaluator ?? throw new ArgumentNullException(nameof(exceptionEvaluator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken = default)
        {
            int attempts = 1;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await action();
                }
                catch (CircuitBreakerOpenException e)
                {
                    // An open circuit refused to run the action; it is not a fault the action raised, so it
                    // never goes to the retry exception evaluator. It still consumes retry budget.
                    attempts = await ConsumeAttemptOrThrowAsync(e, attempts, cancellationToken);
                }
                catch (Exception e)
                {
                    if (!_exceptionEvaluator.ShouldRetry(e))
                    {
                        _logger.LogTrace(e, $"Retry aborted. Exception type '{e.GetType().FullName}' not configured for retry.");
                        throw;
                    }

                    attempts = await ConsumeAttemptOrThrowAsync(e, attempts, cancellationToken);
                }
            }
        }

        // INVARIANT: the loop's single progress-accounting site. Every catch continues here, so no failure —
        // an action fault or a circuit-breaker refusal — can re-enter the loop without spending an attempt.
        private async Task<int> ConsumeAttemptOrThrowAsync(Exception failure, int attempts, CancellationToken cancellationToken)
        {
            if (attempts >= _options.MaxRetryAttempts)
            {
                _logger.LogInformation($"Action was unsuccessful after max ({attempts}) retries.");
                throw new MaxRetryAttemptsExceededException(failure, attempts);
            }

            _logger.LogTrace(failure, $"Retry attempt {attempts} of {_options.MaxRetryAttempts}");
            await _delayedRecovery.ExecuteAsync(attempts, cancellationToken);
            return attempts + 1;
        }
    }
}
