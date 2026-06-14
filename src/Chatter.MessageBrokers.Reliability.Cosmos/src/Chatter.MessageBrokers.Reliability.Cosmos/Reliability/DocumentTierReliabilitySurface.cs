namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The scoped document-tier reliability surface: the DI-scoped home for the active
    /// <see cref="ICosmosAtomicWriteHandle"/>. The Document-Tier Batch-Lifecycle Behavior places the handle here before
    /// <c>next()</c>; the handler and the shared enqueue contract resolve it from here to contribute ops to the same
    /// framework-owned batch (#219/#220). This is the surface-ownership home — NOT
    /// <see cref="MessageBrokers.Context.TransactionContext.Container"/> (ADR-0006 amendment).
    /// </summary>
    public interface IDocumentTierReliabilitySurface
    {
        /// <summary>
        /// The active atomic-write handle for the in-flight message, or <c>null</c> outside an open batch scope.
        /// </summary>
        ICosmosAtomicWriteHandle CurrentHandle { get; }
    }

    /// <summary>
    /// Scoped, mutable implementation of <see cref="IDocumentTierReliabilitySurface"/>. The Document-Tier
    /// Batch-Lifecycle Behavior sets <see cref="CurrentHandle"/> for the duration of <c>next()</c>.
    /// </summary>
    public sealed class DocumentTierReliabilitySurface : IDocumentTierReliabilitySurface
    {
        public ICosmosAtomicWriteHandle CurrentHandle { get; set; }
    }
}
