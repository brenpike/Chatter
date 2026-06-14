namespace Chatter.MessageBrokers.Reliability
{
    /// <summary>
    /// The tier-neutral abstraction the enqueue contract (<c>SendToOutbox</c>) is abstracted over.
    /// Satisfied by the relational <see cref="IPersistanceTransaction"/> on the relational tier and by the
    /// document-tier atomic-write handle on the document tier. This is an empty marker: the two tiers satisfy
    /// the same abstract contract with provider-shaped handles and add no shared members.
    /// </summary>
    public interface IAtomicWriteHandle
    {
    }
}
