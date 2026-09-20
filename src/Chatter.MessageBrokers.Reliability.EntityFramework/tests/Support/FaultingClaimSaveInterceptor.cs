using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// Raised in place of the first modifying statement <see cref="FaultingClaimSaveInterceptor"/> sees.
    /// Deliberately its own type, and deliberately NOT a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>:
    /// a concurrency conflict takes BrokeredMessageOutbox's compensation path, which resets the conflicted entry and
    /// leaves the change tracker carrying nothing, and that is a different exit from the one a test using this hook
    /// is driving.
    /// </summary>
    public sealed class ClaimSaveFaultException : Exception
    {
        public ClaimSaveFaultException()
            : base("Simulated failure of the outbox claim's modifying statement.")
        { }
    }

    /// <summary>
    /// Fails the FIRST modifying statement a context issues and lets every later one through, so a test can drive
    /// the exit where a message reaches the broker and the claim that follows it does not commit. Installed with
    /// <c>optionsBuilder.AddInterceptors(...)</c> on the draining context only; a seeding or verifying context must
    /// use the plain harness overload, or its own writes would take the injected failure instead.
    ///
    /// INVARIANT: the fault is raised on the statement itself rather than on the transaction, so the change tracker
    /// is left exactly as a real failed claim leaves it - the message still Modified, still carrying the processed
    /// stamp as its current value. A transaction-level fault would skip the statement and prove nothing about
    /// residue.
    ///
    /// INVARIANT: only the asynchronous overloads are intercepted, because every write a drain makes - the unit of
    /// work's SaveChangesAsync and the attempt's ExecuteUpdateAsync - is asynchronous. A synchronous write, which is
    /// how the harness seeds, passes through untouched by design.
    ///
    /// INVARIANT: <see cref="InjectedFailureCount"/> is what makes a test using this hook non-vacuous. The UPDATE
    /// heuristic matches provider-generated SQL, which is not this repository's to keep stable, so a test MUST
    /// assert this count rather than trust that the injection happened.
    /// </summary>
    public sealed class FaultingClaimSaveInterceptor : DbCommandInterceptor
    {
        private int _observedModificationCount;
        private int _injectedFailureCount;

        /// <summary>
        /// How many modifying statements reached this interceptor, the failed one included. It counts every write
        /// the drain makes, not only its claims, so a test can assert that no attempt was also written.
        /// </summary>
        public int ObservedModificationCount => _observedModificationCount;

        /// <summary>
        /// How many statements this interceptor actually failed. Zero means the hook never fired and any test
        /// relying on it proved nothing.
        /// </summary>
        public int InjectedFailureCount => _injectedFailureCount;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
                                                                                  CommandEventData eventData,
                                                                                  InterceptionResult<int> result,
                                                                                  CancellationToken cancellationToken = default)
        {
            FailTheFirstModification(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                                                                                         CommandEventData eventData,
                                                                                         InterceptionResult<DbDataReader> result,
                                                                                         CancellationToken cancellationToken = default)
        {
            FailTheFirstModification(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        // Both execution paths are covered because a modification batch may be dispatched through either one, and
        // which one a provider picks depends on whether the generated statement carries a result set to read back.
        private void FailTheFirstModification(DbCommand command)
        {
            if (!command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _observedModificationCount++;

            if (_observedModificationCount > 1)
            {
                return;
            }

            _injectedFailureCount++;
            throw new ClaimSaveFaultException();
        }
    }
}
