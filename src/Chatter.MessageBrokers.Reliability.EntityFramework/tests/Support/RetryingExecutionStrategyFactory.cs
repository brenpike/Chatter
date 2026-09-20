using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// Replaces EF's <see cref="IExecutionStrategyFactory"/> so a context configured over any provider - including
    /// the InMemory provider - hands the unit of work a strategy that reports <c>RetriesOnFailure</c>. Lets the
    /// refusal be proven without a relational provider, a native SQLite library, or a SQL Server container.
    /// </summary>
    public sealed class RetryingExecutionStrategyFactory : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new RetryingExecutionStrategy();
    }

    /// <summary>
    /// A strategy that claims to retry but executes the operation exactly once. The claim is the whole point: the
    /// unit of work must refuse on <see cref="RetriesOnFailure"/> alone, before it ever hands an operation over.
    /// </summary>
    public sealed class RetryingExecutionStrategy : IExecutionStrategy
    {
        public bool RetriesOnFailure => true;

        public TResult Execute<TState, TResult>(
            TState state,
            Func<DbContext, TState, TResult> operation,
            Func<DbContext, TState, ExecutionResult<TResult>> verifySucceeded)
            => operation(null, state);

        public Task<TResult> ExecuteAsync<TState, TResult>(
            TState state,
            Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
            Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>> verifySucceeded,
            CancellationToken cancellationToken = default)
            => operation(null, state, cancellationToken);
    }
}
