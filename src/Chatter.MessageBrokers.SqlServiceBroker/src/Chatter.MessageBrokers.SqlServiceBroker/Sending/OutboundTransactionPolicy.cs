using Chatter.MessageBrokers.Receiving;

namespace Chatter.MessageBrokers.SqlServiceBroker.Sending
{
    /// <summary>
    /// Where the <see cref="SqlServiceBrokerSender"/> obtains the <see cref="Microsoft.Data.SqlClient.SqlConnection"/>
    /// it dispatches on.
    /// </summary>
    internal enum OutboundConnectionOrigin
    {
        /// <summary>Reuse the connection owned by the caller's context <see cref="Microsoft.Data.SqlClient.SqlTransaction"/>.</summary>
        ReuseContext,

        /// <summary>Open a fresh connection from the configured connection string.</summary>
        NewConnection
    }

    /// <summary>
    /// The transaction-enlistment decision the <see cref="SqlServiceBrokerSender"/> acts on for a dispatch.
    /// </summary>
    internal readonly struct OutboundTransactionDecision
    {
        public OutboundTransactionDecision(bool useContextTransaction, OutboundConnectionOrigin connectionOrigin)
        {
            UseContextTransaction = useContextTransaction;
            ConnectionOrigin = connectionOrigin;
        }

        /// <summary>
        /// When true, the dispatch enlists in the caller's context transaction and must NOT commit, roll back, or
        /// dispose it. When false, the dispatch owns its transaction/connection lifecycle end to end.
        /// </summary>
        public bool UseContextTransaction { get; }

        /// <summary>Where the dispatch's connection comes from.</summary>
        public OutboundConnectionOrigin ConnectionOrigin { get; }
    }

    /// <summary>
    /// Pure policy that decides how the <see cref="SqlServiceBrokerSender"/> enlists in (or owns) a transaction for a
    /// dispatch. Extracted VERBATIM from the sender's inline decision: enlist in the caller's transaction only when the
    /// context transaction mode is <see cref="TransactionMode.FullAtomicityViaInfrastructure"/> AND a context
    /// <see cref="Microsoft.Data.SqlClient.SqlTransaction"/> is present; otherwise open a new connection and own the
    /// transaction.
    /// </summary>
    /// <remarks>No SQL, no connection, no I/O, no ambient/static state — inputs in, decision out.</remarks>
    internal static class OutboundTransactionPolicy
    {
        public static OutboundTransactionDecision Resolve(TransactionMode contextTransactionMode, bool hasContextTransaction)
        {
            var useContextTransaction = contextTransactionMode == TransactionMode.FullAtomicityViaInfrastructure && hasContextTransaction;

            var connectionOrigin = useContextTransaction
                                        ? OutboundConnectionOrigin.ReuseContext
                                        : OutboundConnectionOrigin.NewConnection;

            return new OutboundTransactionDecision(useContextTransaction, connectionOrigin);
        }
    }
}
