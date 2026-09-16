using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// Fails the compensating <c>GetDatabaseValuesAsync</c> read that runs after an outbox claim loses an
    /// optimistic-concurrency race, so a test can prove the original <c>DbUpdateConcurrencyException</c> still
    /// propagates rather than being replaced by the read's own failure.
    ///
    /// INVARIANT: the interceptor arms on the concurrency-token UPDATE and then throws on the NEXT command
    /// containing SELECT. That next command is the compensating read - the intervening transaction rollback and
    /// dispose are not DbCommands and never reach this interceptor. Both the non-query and the reader path arm,
    /// because a modification batch may be dispatched through either one.
    ///
    /// Attach it to the LOSING context only; a seeding or winning context must use the plain harness overload.
    ///
    /// INVARIANT: <see cref="InjectedFailureCount"/> is what makes a test using this hook non-vacuous. Both the
    /// guarded and the unguarded compensation end in <c>DbUpdateConcurrencyException</c> when no failure is
    /// injected, so asserting that exception alone cannot tell "the guard held" from "the fault never fired".
    /// The arming and SELECT heuristics match provider-generated SQL, which is not this repository's to keep
    /// stable, so a test MUST assert this count rather than trust that the injection happened.
    /// </summary>
    public sealed class FailingCompensationReadInterceptor : DbCommandInterceptor
    {
        private bool _armed;
        private int _injectedFailureCount;

        /// <summary>
        /// How many reads this interceptor actually failed. Zero means the hook never fired and any test relying
        /// on it proved nothing.
        /// </summary>
        public int InjectedFailureCount => _injectedFailureCount;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
                                                                                  CommandEventData eventData,
                                                                                  InterceptionResult<int> result,
                                                                                  CancellationToken cancellationToken = default)
        {
            ArmWhenUpdating(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                                                                                         CommandEventData eventData,
                                                                                         InterceptionResult<DbDataReader> result,
                                                                                         CancellationToken cancellationToken = default)
        {
            // INVARIANT: the armed check runs BEFORE arming, so the UPDATE batch - which carries its own
            // SELECT @@ROWCOUNT - arms without failing itself.
            if (_armed && command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                _injectedFailureCount++;
                throw new CompensationReadFailedException();
            }

            ArmWhenUpdating(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void ArmWhenUpdating(DbCommand command)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                _armed = true;
            }
        }

        private sealed class CompensationReadFailedException : Exception
        {
            public CompensationReadFailedException()
                : base("Simulated failure of the compensating outbox read.")
            { }
        }
    }
}
