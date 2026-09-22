using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// Raised in place of the claim <see cref="FaultingClaimSaveInterceptor"/> faults.
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
    /// Fails the FIRST statement a context issues that WRITES the processed stamp and lets every later one through,
    /// so a test can drive the exit where a message reaches the broker and the claim that follows it does not
    /// commit. Installed with <c>optionsBuilder.AddInterceptors(...)</c> on the draining context only; a seeding or
    /// verifying context must use the plain harness overload, or its own writes would take the injected failure
    /// instead.
    ///
    /// INVARIANT: the statement to fault is chosen by what it ASSIGNS, never by its position among the writes a
    /// drain makes. The claim ADR-0031 Option D names is the one writing
    /// <see cref="OutboxMessage.ProcessedFromOutboxAtUtc"/>, and a drain issues other modifying statements around
    /// it - the DRAIN CLAIM that arbitrates the row before the publish, and the dispatch attempt recorded after a
    /// failure - which both write NextAttemptAtUtc and neither of which assigns the processed stamp. Faulting "the
    /// first modifying statement" instead reddens
    /// <c>WhenReclaimingAfterAFailedClaimOverSqlite.MustLeaveTheRowProcessedAndSpendNoAttemptWhenTheClaimFailsAfterAPublish</c>
    /// (measured), because the drain claim now precedes the claim this hook means to fault, so the fault lands on
    /// the drain claim, the message never reaches the broker and the row ends unprocessed. Any positional rule
    /// re-breaks the same way the next time a statement is added ahead of the claim.
    ///
    /// INVARIANT: the ASSIGNMENT CLAUSE alone is searched, never the whole statement. The drain claim's PREDICATE
    /// names <see cref="OutboxMessage.ProcessedFromOutboxAtUtc"/> as well - it claims only an unprocessed row - so a
    /// statement-wide search matches it and restores the positional behaviour above. Same mutation, same oracle.
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
    /// INVARIANT: <see cref="InjectedFailureCount"/> is what makes a test using this hook non-vacuous. The match
    /// reads provider-generated SQL, which is not this repository's to keep stable, so a test MUST assert this
    /// count rather than trust that the injection happened.
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
            FailTheFirstProcessedStampWrite(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                                                                                         CommandEventData eventData,
                                                                                         InterceptionResult<DbDataReader> result,
                                                                                         CancellationToken cancellationToken = default)
        {
            FailTheFirstProcessedStampWrite(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        // Both execution paths are covered because a modification batch may be dispatched through either one, and
        // which one a provider picks depends on whether the generated statement carries a result set to read back.
        private void FailTheFirstProcessedStampWrite(DbCommand command)
        {
            if (!command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _observedModificationCount++;

            if (!AssignsTheProcessedStamp(command.CommandText) || _injectedFailureCount > 0)
            {
                return;
            }

            _injectedFailureCount++;
            throw new ClaimSaveFaultException();
        }

        /// <summary>
        /// Reports whether a modifying statement WRITES <see cref="OutboxMessage.ProcessedFromOutboxAtUtc"/>, by
        /// reading its assignment clause alone - everything from SET up to the predicate that follows it, or to the
        /// end of the statement where there is none.
        /// </summary>
        private static bool AssignsTheProcessedStamp(string commandText)
        {
            var assignmentsAt = commandText.IndexOf("SET", StringComparison.OrdinalIgnoreCase);
            if (assignmentsAt < 0)
            {
                return false;
            }

            var predicateAt = commandText.IndexOf("WHERE", assignmentsAt, StringComparison.OrdinalIgnoreCase);
            var assignments = predicateAt < 0
                ? commandText.Substring(assignmentsAt)
                : commandText.Substring(assignmentsAt, predicateAt - assignmentsAt);

            return assignments.Contains(nameof(OutboxMessage.ProcessedFromOutboxAtUtc), StringComparison.Ordinal);
        }
    }
}
