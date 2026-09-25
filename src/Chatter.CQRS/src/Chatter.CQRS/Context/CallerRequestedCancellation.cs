using System;
using System.Threading;

namespace Chatter.CQRS.Context
{
    /// <summary>
    /// Decides whether a dispatch fault is the routine outcome of the caller cancelling the dispatch, rather than a
    /// dispatch error. See ADR-0040.
    /// </summary>
    internal static class CallerRequestedCancellation
    {
        /// <summary>
        /// Returns <see langword="true"/> only when <paramref name="fault"/> is an <see cref="OperationCanceledException"/>
        /// and the <see cref="IMessageHandlerContext.CancellationToken"/> of <paramref name="context"/> is signalled.
        /// </summary>
        internal static bool Explains(Exception fault, IMessageHandlerContext context)
            => context != null && ExplainsFault(fault, context.CancellationToken);

        /// <summary>
        /// Returns <see langword="true"/> only when <paramref name="fault"/> is an <see cref="OperationCanceledException"/>
        /// and the <see cref="IQueryHandlerContext.CancellationToken"/> of <paramref name="context"/> is signalled.
        /// </summary>
        internal static bool Explains(Exception fault, IQueryHandlerContext context)
            => context != null && ExplainsFault(fault, context.CancellationToken);

        // INVARIANT: a cancellation fault is routine only while the caller's token is signalled; a spontaneous one
        // (for example an HttpClient timeout) stays a dispatch error. Pinned by
        // WhenDeciding.MustNotExplainOperationCanceledExceptionWhenTokenIsNotSignalled, which goes red when the
        // IsCancellationRequested conjunct is dropped. Rationale: ADR-0040.
        private static bool ExplainsFault(Exception fault, CancellationToken cancellationToken)
            => fault is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }
}
