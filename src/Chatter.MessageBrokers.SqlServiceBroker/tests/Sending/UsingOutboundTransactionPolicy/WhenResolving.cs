using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Sending;
using FluentAssertions;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Sending.UsingOutboundTransactionPolicy
{
    // Characterization decision-table for OutboundTransactionPolicy.Resolve, extracted VERBATIM from
    // SqlServiceBrokerSender.Dispatch:
    //   useContextTransaction = (TransactionMode == FullAtomicityViaInfrastructure && contextTransaction != null)
    //   connection origin      = useContextTransaction ? ReuseContext : NewConnection
    //
    // Every combination of TransactionMode x (context transaction present | absent) is exercised so the
    // policy is pinned to today's behaviour before STEP-006/007 move the sender behind the port.
    public class WhenResolving : Testing.Core.Context
    {
        // INVARIANT: these rows pass internal production types (OutboundConnectionOrigin) and so the
        // consuming theory method is internal (not public); a public method with a less-accessible
        // parameter type trips CS0051. The test assembly sees the types via InternalsVisibleTo.
        public static IEnumerable<object[]> DecisionRows()
        {
            // FullAtomicityViaInfrastructure + present context transaction -> reuse the caller's transaction.
            yield return new object[] {
                TransactionMode.FullAtomicityViaInfrastructure, true,
                true, OutboundConnectionOrigin.ReuseContext,
                "FullAtomicityViaInfrastructure with a context transaction must reuse the caller's transaction" };

            // FullAtomicityViaInfrastructure but no context transaction -> own a new connection/transaction.
            yield return new object[] {
                TransactionMode.FullAtomicityViaInfrastructure, false,
                false, OutboundConnectionOrigin.NewConnection,
                "FullAtomicityViaInfrastructure without a context transaction must open a new connection" };

            // ReceiveOnly + present context transaction -> still own a new connection/transaction.
            yield return new object[] {
                TransactionMode.ReceiveOnly, true,
                false, OutboundConnectionOrigin.NewConnection,
                "ReceiveOnly must open a new connection even when a context transaction is present" };

            // ReceiveOnly + no context transaction -> own a new connection/transaction.
            yield return new object[] {
                TransactionMode.ReceiveOnly, false,
                false, OutboundConnectionOrigin.NewConnection,
                "ReceiveOnly without a context transaction must open a new connection" };

            // None + present context transaction -> still own a new connection/transaction.
            yield return new object[] {
                TransactionMode.None, true,
                false, OutboundConnectionOrigin.NewConnection,
                "None must open a new connection even when a context transaction is present" };

            // None + no context transaction -> own a new connection/transaction.
            yield return new object[] {
                TransactionMode.None, false,
                false, OutboundConnectionOrigin.NewConnection,
                "None without a context transaction must open a new connection" };
        }

        [Theory]
        [MemberData(nameof(DecisionRows))]
        internal void MustResolveExpectedDecision(
            TransactionMode contextTransactionMode,
            bool hasContextTransaction,
            bool expectedUseContextTransaction,
            OutboundConnectionOrigin expectedConnectionOrigin,
            string because)
        {
            var decision = OutboundTransactionPolicy.Resolve(contextTransactionMode, hasContextTransaction);

            decision.UseContextTransaction.Should().Be(expectedUseContextTransaction, because);
            decision.ConnectionOrigin.Should().Be(expectedConnectionOrigin, because);
        }
    }
}
