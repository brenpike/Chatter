using System;

namespace Chatter.Testing.Core.Creators.MessageBrokers.Recovery
{
    /// <summary>
    /// A distinct exception type used by Recovery characterization tests so predicate-match
    /// behavior can be pinned against a known type without relying on production exception types.
    /// </summary>
    public sealed class FakeRecoverableException : Exception
    {
        public FakeRecoverableException()
        {
        }

        public FakeRecoverableException(string message)
            : base(message)
        {
        }
    }
}
