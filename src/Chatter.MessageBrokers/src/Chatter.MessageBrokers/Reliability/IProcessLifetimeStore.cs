namespace Chatter.MessageBrokers.Reliability
{
    /// <summary>
    /// Marks a SHIPPED in-memory reliability store whose dictionary is the process's only copy of that state, so the
    /// store is registered once per process rather than once per operation.
    /// </summary>
    /// <remarks>
    /// This marker is a SIGNPOST, not a guard. It documents at the store why its instance must outlive any DI scope
    /// and it gives <c>AddProcessLifetimeDefault</c> something to constrain on, but it enforces nothing on its own:
    /// <c>AddIfNotRegistered&lt;TService, TImplementation&gt;(ServiceLifetime.Scoped)</c> stays callable for a type
    /// carrying this marker, so a future registration can still get the lifetime wrong.
    /// The ACTUAL enforcement is the cross-scope oracle
    /// (<c>WhenSharingReliabilityStoresAcrossScopes</c>): it resolves each shipped default from two scopes opened off
    /// <see cref="Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"/> the way every consumer opens
    /// them, sends a row in one scope and drains it in another, and receives one message id in two scopes — so a
    /// per-operation instance fails on the observable consequence rather than on a declared lifetime.
    /// A store whose state does NOT live in the instance — a relational or document provider store, whose state lives
    /// in the DbContext or in the store itself — must NOT carry this marker; its per-operation lifetime is correct.
    /// </remarks>
    internal interface IProcessLifetimeStore
    {
    }
}
