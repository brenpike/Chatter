using Microsoft.EntityFrameworkCore.Storage;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    /// <summary>
    /// Records, against the <see cref="IDbContextTransaction"/> an inbox claim was flushed into, whether that
    /// claim's handler returned. A claim is opened UNSETTLED and is settled only by the handler returning, so the
    /// commit point reads the claim's own recorded outcome rather than inferring one from whether an exception
    /// happened to reach it.
    /// </summary>
    // INVARIANT: the register is static rather than an injected collaborator. BrokeredMessageInbox&lt;TContext&gt;
    // is public and hand-constructed - by the suite and by consumers - so a channel supplied through its
    // constructor would be absent wherever it was not supplied, and a commit point reading an absent channel would
    // find every claim settled. No test pins this choice - a constructor parameter that does not exist cannot be
    // left unsupplied by a fact - but MustRefuseTheCommitWhenAHandlerSwallowedAClaimedMessagesFailure and
    // MustRefuseTheCommitWhenOnlyOneOfTwoClaimsWasSwallowed both construct the inbox by hand, so they exercise the
    // path a consumer takes rather than one a container wired up.
    //
    // INVARIANT: the key is the IDbContextTransaction OBJECT, held WEAKLY, and the value never references it back.
    // UnitOfWork.CurrentTransaction and UnitOfWork.BeginAsync each build a FRESH PersistanceTransaction over the
    // same provider transaction, so state keyed on the wrapper would be lost between the inbox's flush and the
    // commit; and a value holding its own key keeps a ConditionalWeakTable entry alive forever, so an abandoned
    // transaction carrying an unsettled claim would leak. UnsettledClaim therefore carries only strings.
    // Keying any coarser than one transaction lets an abandoned claim refuse a commit that has nothing to do with
    // it: serving every transaction from one shared collection reddens fifteen facts, among them
    // MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAFreshMessageId, which runs a second
    // ExecuteAsync over the SAME context so the first delivery's unsettled claim meets the second's commit, and
    // MustCommitTheClaimWhenTheHandlerReturns, measured by making that substitution and counting. Keying on the
    // TContext specifically is not expressible: PersistanceTransaction holds a transaction and no context.
    internal static class InboxClaimRegister
    {
        private static readonly ConditionalWeakTable<IDbContextTransaction, UnsettledClaims> ClaimsByTransaction =
            new ConditionalWeakTable<IDbContextTransaction, UnsettledClaims>();

        public static UnsettledClaim Open(IDbContextTransaction transaction, string messageId, string contextTypeName)
        {
            var claim = new UnsettledClaim(messageId, contextTypeName);
            var unsettled = ClaimsByTransaction.GetOrCreateValue(transaction);

            lock (unsettled)
            {
                unsettled.Claims.Add(claim);
            }

            return claim;
        }

        // INVARIANT: settlement removes THAT claim and no other. A dispatch nested inside another handler opens more
        // than one claim on one transaction, so the per-transaction entry holds a collection and each claim is
        // removed by its own identity - UnsettledClaim overrides no equality, so the removal below is a reference
        // match. Oracle: MustRefuseTheCommitWhenOnlyOneOfTwoClaimsWasSwallowed, which settles a LATER claim on a
        // transaction whose EARLIER claim went unsettled; collapsing this collection to a per-transaction flag that
        // the last settlement clears reddens that fact and no other.
        public static void Settle(IDbContextTransaction transaction, UnsettledClaim claim)
        {
            if (!ClaimsByTransaction.TryGetValue(transaction, out var unsettled))
            {
                return;
            }

            lock (unsettled)
            {
                unsettled.Claims.Remove(claim);
            }
        }

        public static bool HasUnsettledClaim(IDbContextTransaction transaction, out string unsettledClaims)
        {
            unsettledClaims = null;

            if (transaction is null || !ClaimsByTransaction.TryGetValue(transaction, out var unsettled))
            {
                return false;
            }

            lock (unsettled)
            {
                if (unsettled.Claims.Count == 0)
                {
                    return false;
                }

                unsettledClaims = string.Join(", ", unsettled.Claims.Select(unsettledClaim => unsettledClaim.Describe()));

                return true;
            }
        }

        private sealed class UnsettledClaims
        {
            public List<UnsettledClaim> Claims { get; } = new List<UnsettledClaim>();
        }
    }

    /// <summary>
    /// A claim the inbox flushed and whose handler has not returned. Carries only what the commit point's refusal
    /// must name, so it can be held by the register without keeping its transaction alive.
    /// </summary>
    internal sealed class UnsettledClaim
    {
        private readonly string _messageId;
        private readonly string _contextTypeName;

        internal UnsettledClaim(string messageId, string contextTypeName)
        {
            _messageId = messageId;
            _contextTypeName = contextTypeName;
        }

        internal string Describe()
            => $"message id '{_messageId}' claimed by BrokeredMessageInbox<{_contextTypeName}>";
    }
}
