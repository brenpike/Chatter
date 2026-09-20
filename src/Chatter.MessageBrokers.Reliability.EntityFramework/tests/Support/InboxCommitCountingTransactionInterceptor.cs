using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.EntityFramework.Tests.Support
{
    /// <summary>
    /// Counts every transaction commit a context performs, so a test can separate "the inbox flushed its claim"
    /// from "the inbox committed it". Installed with <c>optionsBuilder.AddInterceptors(...)</c> on the context the
    /// inbox is given.
    ///
    /// INVARIANT: both the synchronous and the asynchronous committing hook increment, because which one runs is
    /// decided by the caller that commits, not by the inbox.
    ///
    /// INVARIANT: the interception point is the commit itself, not the save, so a SaveChangesAsync that flushes
    /// into an open transaction leaves <see cref="CommitCount"/> untouched. A save counter could not tell a flush
    /// from a commit, which is the whole distinction a test using this hook is drawing.
    ///
    /// INVARIANT: a test asserting <see cref="CommitCount"/> is zero is vacuous on its own - an interceptor that
    /// was never wired up also counts zero - so such a test MUST go on to commit and observe the count rise.
    /// </summary>
    public sealed class InboxCommitCountingTransactionInterceptor : DbTransactionInterceptor
    {
        private int _commitCount;

        /// <summary>
        /// How many transaction commits this interceptor observed.
        /// </summary>
        public int CommitCount => _commitCount;

        public override InterceptionResult TransactionCommitting(DbTransaction transaction,
                                                                 TransactionEventData eventData,
                                                                 InterceptionResult result)
        {
            Interlocked.Increment(ref _commitCount);
            return base.TransactionCommitting(transaction, eventData, result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
                                                                                 TransactionEventData eventData,
                                                                                 InterceptionResult result,
                                                                                 CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commitCount);
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }
}
