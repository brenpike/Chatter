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
        public void Register(string topLevelEntity, TransactionMode? transactionMode)
        {
            _receivers.Add(new RegisteredReceiver(topLevelEntity, transactionMode));
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

        private readonly struct RegisteredReceiver
        {
            public RegisteredReceiver(string topLevelEntity, TransactionMode? transactionMode)
            {
                TopLevelEntity = topLevelEntity;
                TransactionMode = transactionMode;
            }

            public string TopLevelEntity { get; }
            public TransactionMode? TransactionMode { get; }
        }
    }
}
