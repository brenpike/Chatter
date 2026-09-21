using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// Raised in place of the inbox claim's flush by <see cref="FaultingInboxClaimFlushInterceptor"/> and by
    /// <see cref="FaultingInboxClaimSaveInterceptor"/>. Deliberately NOT a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>: <c>TryClaimMessageIdAsync</c> catches that
    /// type, and the exits these two hooks drive are the ones that catch does not settle.
    /// </summary>
    public sealed class InboxClaimFlushFaultException : Exception
    {
        public InboxClaimFlushFaultException()
            : base("Simulated failure of the inbox claim's flush.")
        { }
    }

    /// <summary>
    /// Fails the FIRST inbox write carrying a named message id and lets every later statement through, so a test can
    /// drive a flush that reaches the store and fails there. Installed with
    /// <c>optionsBuilder.AddInterceptors(...)</c> on the context the inbox is given; a seeding or verifying context
    /// must use the plain harness overload, or its own writes would take the injected failure instead.
    ///
    /// Which exit of <c>TryClaimMessageIdAsync</c> is reached is decided by WHICH message id is named, because EF
    /// Core wraps a statement-level failure into a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>
    /// whose <c>Entries</c> carries the ONE entry of the failing statement - measured on
    /// Microsoft.EntityFrameworkCore.Sqlite 8.0.30 and 10.0.11, for a fresh id's INSERT and an expired marker's
    /// UPDATE alike, and for a provider exception and a plain one alike:
    ///
    /// Naming the CLAIM's own message id puts the claim in that single entry, so the absorption gate matches and the
    /// claim is detached and the store re-read - the concurrent-delivery path.
    ///
    /// Naming a COMPANION row staged in the same change tracker puts that companion in the single entry instead, so
    /// the absorption gate does not match, <c>TryClaimMessageIdAsync</c> rethrows before its detach, and the claim
    /// survives in the change tracker - the exit where a redelivery over this same context resolves a phantom claim
    /// from the identity map. A lone claim cannot reach that exit: the gate matches whenever the claim is the only
    /// entry, which is why a test driving it stages a companion the way a dispatch nested inside another handler
    /// does.
    ///
    /// INVARIANT: the statement is identified by the <c>ReceivedByInboxAtUtc</c> column paired with an INSERT or
    /// UPDATE keyword. The column is this repository's own property name, unlike the table name, which a DbSet
    /// property renames, and unlike the rest of the statement, which the provider generates; the keyword is what
    /// separates the claim's write from the FindAsync read naming that same column. The message id is read from the
    /// command's PARAMETERS rather than its text because a parameterised statement carries no literal id.
    ///
    /// INVARIANT: only the asynchronous overloads are intercepted, because every write a claim makes is
    /// asynchronous. A synchronous write, which is how a harness seeds, passes through untouched by design.
    ///
    /// INVARIANT: <see cref="InjectedFailureCount"/> is what makes a test using this hook non-vacuous. A test
    /// asserting an empty change tracker passes identically when this hook never fired, because the claim would
    /// then simply have committed, so such a test MUST assert this count rather than trust that the injection
    /// happened.
    /// </summary>
    public sealed class FaultingInboxClaimFlushInterceptor : DbCommandInterceptor
    {
        private const string InboxColumnName = "ReceivedByInboxAtUtc";

        private readonly string _messageIdToFault;
        private int _observedInboxWriteCount;
        private int _injectedFailureCount;

        /// <param name="messageIdToFault">
        /// The message id whose inbox write is failed - the claim's own id to drive absorption, a companion row's id
        /// to drive the rethrow that leaves the claim tracked.
        /// </param>
        public FaultingInboxClaimFlushInterceptor(string messageIdToFault)
            => _messageIdToFault = messageIdToFault ?? throw new ArgumentNullException(nameof(messageIdToFault));

        /// <summary>
        /// How many inbox writes carrying the named message id reached this interceptor, the failed one included.
        /// </summary>
        public int ObservedInboxWriteCount => _observedInboxWriteCount;

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
            FailTheFirstWriteOfTheNamedMessageId(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                                                                                         CommandEventData eventData,
                                                                                         InterceptionResult<DbDataReader> result,
                                                                                         CancellationToken cancellationToken = default)
        {
            FailTheFirstWriteOfTheNamedMessageId(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        // Both execution paths are covered because a modification batch may be dispatched through either one, and
        // which one a provider picks depends on whether the generated statement carries a result set to read back.
        private void FailTheFirstWriteOfTheNamedMessageId(DbCommand command)
        {
            if (!IsInboxWrite(command) || !CarriesTheNamedMessageId(command))
            {
                return;
            }

            _observedInboxWriteCount++;

            if (_observedInboxWriteCount > 1)
            {
                return;
            }

            _injectedFailureCount++;
            throw new InboxClaimFlushFaultException();
        }

        private static bool IsInboxWrite(DbCommand command)
        {
            var commandText = command.CommandText;

            return commandText.Contains(InboxColumnName, StringComparison.Ordinal)
                   && (commandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                       || commandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
        }

        private bool CarriesTheNamedMessageId(DbCommand command)
        {
            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter.Value is string parameterValue
                    && string.Equals(parameterValue, _messageIdToFault, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Fails the FIRST <c>SaveChangesAsync</c> a context performs and lets every later one through, so a test can
    /// drive the exit where the claim's flush raises something <c>TryClaimMessageIdAsync</c>'s
    /// <c>catch (DbUpdateException)</c> cannot catch. Installed with <c>optionsBuilder.AddInterceptors(...)</c> on
    /// the context the inbox is given.
    ///
    /// INVARIANT: the fault is an <see cref="InboxClaimFlushFaultException"/>, which is not a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>, so it escapes
    /// <c>TryClaimMessageIdAsync</c> whole and reaches the caller with the claim still tracked - Added for a fresh
    /// message id, Modified carrying the refreshed timestamp as its current value for an expired one. Both states
    /// measured on Microsoft.EntityFrameworkCore.Sqlite 8.0.30 and 10.0.11. A cancellation raised from the statement
    /// escapes by the same route, because EF Core wraps a statement failure into a DbUpdateException but passes an
    /// <see cref="OperationCanceledException"/> through untouched.
    ///
    /// INVARIANT: the interception point is the save, not the statement, so nothing of the claim reaches the store.
    /// A statement-level fault instead produces a DbUpdateException that the claim's own catch handles, which is a
    /// different exit and the one <see cref="FaultingInboxClaimFlushInterceptor"/> serves.
    ///
    /// INVARIANT: <see cref="InjectedFailureCount"/> is what makes a test using this hook non-vacuous, for the same
    /// reason it is on <see cref="FaultingInboxClaimFlushInterceptor"/>: a change tracker assertion reads the same
    /// whether the fault fired or the save simply succeeded, so such a test MUST assert this count.
    /// </summary>
    public sealed class FaultingInboxClaimSaveInterceptor : SaveChangesInterceptor
    {
        private int _observedSaveCount;
        private int _injectedFailureCount;

        /// <summary>
        /// How many saves reached this interceptor, the failed one included.
        /// </summary>
        public int ObservedSaveCount => _observedSaveCount;

        /// <summary>
        /// How many saves this interceptor actually failed. Zero means the hook never fired and any test relying on
        /// it proved nothing.
        /// </summary>
        public int InjectedFailureCount => _injectedFailureCount;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                                                                              InterceptionResult<int> result,
                                                                              CancellationToken cancellationToken = default)
        {
            _observedSaveCount++;

            if (_observedSaveCount > 1)
            {
                return base.SavingChangesAsync(eventData, result, cancellationToken);
            }

            _injectedFailureCount++;
            throw new InboxClaimFlushFaultException();
        }
    }
}
