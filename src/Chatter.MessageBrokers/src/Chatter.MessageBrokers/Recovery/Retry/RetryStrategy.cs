using Chatter.MessageBrokers.Context;
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
            int attempts = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    attempts++;
                    return await action();
                }
                catch (Exception e)
                {
                    //TODO: add logging
                    if (!_exceptionEvaluator.ShouldRetry(e))
                    {
                        throw;
                    }

                    if (attempts <= _options.MaxRetryAttempts)
                    {
                        await _delayedRecovery.ExecuteAsync(attempts);
                    }
                    else
                    {
                        throw new MaxRetryAttemptsExceededException(e, attempts);
                    }
                }
            }
        }
    }
}
