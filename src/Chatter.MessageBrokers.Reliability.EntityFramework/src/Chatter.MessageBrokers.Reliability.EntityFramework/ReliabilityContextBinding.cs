using Microsoft.EntityFrameworkCore;
using System;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    /// <summary>
    /// Records the single <see cref="DbContext"/> that every reliability extension on one command pipeline is bound to.
    /// </summary>
    /// <remarks>
    /// INVARIANT: ONE context spans the whole reliability pipeline. The unit of work commits a context, the inbox writes
    /// its marker into a context, the outbox stages its rows into a context, and retention purges both tables out of a
    /// context. Unless all four name the same one, the unit of work commits a transaction that does not carry the inbox
    /// marker it was meant to make atomic - the once-only guarantee, lost in silence. Each extension takes its own type
    /// argument and the last registration for a service type wins, so a mixed set produces exactly that pipeline without
    /// anything failing. The first extension called registers this binding and every later one reads it, which is what
    /// lets a second context be refused at registration rather than discovered in production.
    /// </remarks>
    internal sealed class ReliabilityContextBinding
    {
        public ReliabilityContextBinding(Type contextType)
            => ContextType = contextType ?? throw new ArgumentNullException(nameof(contextType));

        public Type ContextType { get; }
    }
}
