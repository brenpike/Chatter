using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The document-tier participation allowlist: a singleton registry keyed by command <see cref="Type"/> to its
    /// <see cref="DocumentReliabilityRegistration"/>. Participation IS having a registration (registry-only — there is
    /// NO marker interface). The Document-Tier Batch-Lifecycle Behavior consults <see cref="TryGet"/> on every command;
    /// a non-participant (no registration) is a cheap dictionary miss and bare-passes-through with no resolver call and
    /// no batch (ADR-0008).
    /// </summary>
    public sealed class DocumentReliabilityRegistry
    {
        private readonly Dictionary<Type, DocumentReliabilityRegistration> _registrations = new Dictionary<Type, DocumentReliabilityRegistration>();

        /// <summary>
        /// Looks up the registration for <paramref name="commandType"/>. This is the hot path for non-participants — a
        /// cheap dictionary lookup that returns <c>false</c> with no allocation when the command type is not registered.
        /// </summary>
        /// <returns><c>true</c> and the registration when the command type participates; otherwise <c>false</c>.</returns>
        public bool TryGet(Type commandType, out DocumentReliabilityRegistration registration)
            => _registrations.TryGetValue(commandType ?? throw new ArgumentNullException(nameof(commandType)), out registration);

        /// <summary>
        /// All current registrations, for the #222 relay's per-(Database, ContainerName, LeaseName) change-feed fan-out
        /// (the host dedupes the triple itself — many command types may share one container, so this is NOT one entry per
        /// distinct triple). Internal: only the relay hosted service enumerates registrations; the public surface stays
        /// <see cref="TryGet"/>/<c>Add</c>.
        /// </summary>
        internal IReadOnlyCollection<DocumentReliabilityRegistration> Registrations => _registrations.Values;

        /// <summary>
        /// Adds a registration. Additive across N calls, but rejects a DUPLICATE registration for the same command type
        /// — a clear configuration error (two conflicting container/resolver bindings for one command). Internal: only
        /// the provider's <c>WithCosmosDocumentReliability&lt;TCommand&gt;</c> entry point adds registrations.
        /// </summary>
        /// <exception cref="InvalidOperationException">The command type is already registered.</exception>
        internal void Add(DocumentReliabilityRegistration registration)
        {
            _ = registration ?? throw new ArgumentNullException(nameof(registration));

            if (_registrations.ContainsKey(registration.CommandType))
            {
                throw new InvalidOperationException(
                    $"A document reliability registration already exists for command type '{registration.CommandType.FullName}'. Each command type may be registered exactly once.");
            }

            _registrations.Add(registration.CommandType, registration);
        }
    }
}
