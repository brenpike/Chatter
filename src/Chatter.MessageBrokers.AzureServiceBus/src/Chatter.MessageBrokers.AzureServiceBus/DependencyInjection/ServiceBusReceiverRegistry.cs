using Chatter.MessageBrokers.Receiving;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chatter.MessageBrokers.AzureServiceBus.DependencyInjection
{
    // INVARIANT: a write-at-registration, read-at-client-build registry of the Azure Service Bus receivers
    // configured on a host. AddQueueReceiver/AddTopicSubscription append to it as they run (all complete
    // before the shared ServiceBusClient is first resolved), and the client factory reads it to compute the
    // effective cross-entity-transactions flag and to enforce the single-top-level-entity startup guard.
    // ReceiverOptions are captured here (not resolved from the container) so the singleton client factory
    // never re-enters DI to inspect receivers.
    internal sealed class ServiceBusReceiverRegistry
    {
        private readonly List<RegisteredReceiver> _receivers = new List<RegisteredReceiver>();

        // Records a configured receiver. topLevelEntity is the queue name for a queue receiver or the TOPIC
        // name for a subscription — the unit Azure Service Bus pins a cross-entity transaction to. Two
        // subscriptions on the same topic share one top-level entity and so do not count as distinct.
        // requiresSession marks a receiver as session-mode (set true by AddSessionQueueReceiver/
        // AddSessionTopicSubscription); the receiver factory reads it at client-build time to select the
        // session adapter for the entity.
        public void Register(string topLevelEntity, TransactionMode? transactionMode, bool requiresSession = false)
        {
            _receivers.Add(new RegisteredReceiver(topLevelEntity, transactionMode, requiresSession));
        }

        // True when any configured receiver's EFFECTIVE transaction mode is FullAtomicityViaInfrastructure,
        // which requires cross-entity transactions on the shared client. A receiver registered with no
        // per-call mode (null) inherits the global MessageBrokerOptions.TransactionMode at runtime (mirroring
        // BrokeredMessageReceiver's `options.TransactionMode ??= _messageBrokerOptions.TransactionMode`), so
        // the effective mode folds the global mode in: per-call ?? global.
        public bool AnyRequiresCrossEntityTransactions(TransactionMode globalTransactionMode)
            => _receivers.Any(r => (r.TransactionMode ?? globalTransactionMode) == TransactionMode.FullAtomicityViaInfrastructure);

        // The set of DISTINCT top-level receiver entities (queue names / topic names), case-insensitive to
        // match Azure Service Bus entity-name semantics. Used by the startup guard to detect the
        // unsupportable cross-entity + multiple-top-level-entity combination.
        public IReadOnlyCollection<string> DistinctTopLevelEntities()
            => _receivers
                .Where(r => !string.IsNullOrWhiteSpace(r.TopLevelEntity))
                .Select(r => r.TopLevelEntity)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // True when any receiver registered for the given top-level entity is session-mode, case-insensitive to
        // match Azure Service Bus entity-name semantics (mirroring DistinctTopLevelEntities). The receiver
        // factory (ServiceBusReceiver.CreateProductionReceiver) calls this at client-build time to select the
        // session adapter for a session-mode entity and the existing adapter otherwise.
        public bool RequiresSession(string topLevelEntity)
            => _receivers.Any(r => r.RequiresSession
                && string.Equals(r.TopLevelEntity, topLevelEntity, StringComparison.OrdinalIgnoreCase));

        private readonly struct RegisteredReceiver
        {
            public RegisteredReceiver(string topLevelEntity, TransactionMode? transactionMode, bool requiresSession)
            {
                TopLevelEntity = topLevelEntity;
                TransactionMode = transactionMode;
                RequiresSession = requiresSession;
            }

            public string TopLevelEntity { get; }
            public TransactionMode? TransactionMode { get; }
            public bool RequiresSession { get; }
        }
    }
}
