using Microsoft.EntityFrameworkCore.Storage;
using System.Runtime.CompilerServices;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    /// <summary>
    /// Records, against the <see cref="IDbContextTransaction"/> object a unit of work BEGAN, that a unit of work
    /// owns it, so a caller about to write into a transaction can ask whether the failure and the commit of that
    /// transaction are ones this package controls.
    /// </summary>
    // INVARIANT: ownership is CARRIED, never INFERRED. The entry below is written by UnitOfWork.BeginAsync about
    // the ONE transaction it has just created, and read by BrokeredMessageInbox about the ONE transaction it is
    // flushing into. This is not the re-derivation UnitOfWork.UnitOfWorkTransaction forbids: that rule refuses to
    // decide, at commit or rollback time, whether the context's CURRENT ambient transaction is the one a scope
    // began, a question a nested unit of work makes ambiguous. A lookup keyed on the transaction OBJECT asks
    // nothing about which scope is current. It is the same discipline as BrokeredMessageInbox's "the transaction
    // read is the ONE read, captured and carried on the returned claim".
    //
    // INVARIANT: the register is static rather than an injected collaborator, for the reason recorded on
    // InboxClaimRegister: BrokeredMessageInbox&lt;TContext&gt; is public and hand-constructed, so a channel supplied
    // through its constructor would be absent wherever it was not supplied, and an absent channel would read as
    // "owned" - failing open on exactly the case the reader's refusal exists to stop.
    //
    // INVARIANT: the key is the IDbContextTransaction OBJECT, held WEAKLY, and the value is a bare marker that
    // references nothing. A value holding its own key keeps a ConditionalWeakTable entry alive forever, so every
    // transaction a process ever began would be retained. Keying on the wrapper instead is not open to this
    // register: UnitOfWork.CurrentTransaction and UnitOfWork.BeginAsync each build a FRESH PersistanceTransaction
    // over the same provider transaction, so the reader would never meet the object the writer registered.
    //
    // Oracles: WhenReceivingViaInbox.MustRefuseToClaimInsideATransactionNoUnitOfWorkBegan pins the refusal this
    // register enables, .MustCommitTheClaimWhenTheHandlerReturns pins the grant - dropping the Register call from
    // UnitOfWork.BeginAsync reddens it, so a register that recorded nothing cannot pass for a refusal that is
    // always right - and .MustClaimInsideAUnitOfWorkNestedInAnothersTransaction pins the keying, going red if
    // ownership is recorded against the unit of work SCOPE rather than the transaction object.
    internal static class UnitOfWorkTransactionRegister
    {
        private static readonly ConditionalWeakTable<IDbContextTransaction, OwnedByAUnitOfWork> OwnersByTransaction =
            new ConditionalWeakTable<IDbContextTransaction, OwnedByAUnitOfWork>();

        public static void Register(IDbContextTransaction transaction)
            => OwnersByTransaction.GetOrCreateValue(transaction);

        public static bool IsOwnedByAUnitOfWork(IDbContextTransaction transaction)
            => !(transaction is null) && OwnersByTransaction.TryGetValue(transaction, out _);

        private sealed class OwnedByAUnitOfWork
        {
        }
    }
}
