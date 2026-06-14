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
        // receiverPath is the receiver's OWN path (queue name for a queue receiver; SUBSCRIPTION name for a
        // subscription) — the discriminator that distinguishes two receivers sharing one top-level entity.
        // requiresSession marks a receiver as session-mode (set true by AddSessionQueueReceiver/
        // AddSessionTopicSubscription); the receiver factory reads it at client-build time via the
        // per-receiver RequiresSession(receiverPath, sendingPath) lookup to select the session adapter for
        // THIS receiver (not every receiver sharing the top-level entity).
        public void Register(string topLevelEntity, string receiverPath, TransactionMode? transactionMode, bool requiresSession = false)
        {
            _receivers.Add(new RegisteredReceiver(topLevelEntity, receiverPath, transactionMode, requiresSession));
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

        // True when the SPECIFIC receiver identified by (receiverPath, sendingPath) is session-mode. The match
        // is PER-RECEIVER, not per-top-level-entity: a session-enabled subscription and a normal subscription
        // on the SAME topic are distinct receivers, so the normal one is NOT routed through the session
        // adapter. Both key components compare case-insensitively to match Azure Service Bus entity-name
        // semantics (mirroring DistinctTopLevelEntities). receiverPath is the receiver's own path (queue name
        // or subscription name) and sendingPath is the topic for a subscription (empty/equal-to-receiver-path
        // for a queue); the same pair was supplied at registration. The receiver factory
        // (ServiceBusReceiver.CreateProductionReceiver) calls this at client-build time, and STEP-002's
        // concurrency clamp reuses it to ask "is this receiver path session-mode?".
        public bool RequiresSession(string receiverPath, string sendingPath)
        {
            var topLevelEntity = InferTopLevelEntity(sendingPath, receiverPath);
            return _receivers.Any(r => r.RequiresSession
                && string.Equals(r.MessageReceiverPath, receiverPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.TopLevelEntity, topLevelEntity, StringComparison.OrdinalIgnoreCase));
        }

        // Mirrors the registration-time top-level-entity inference (and ServiceBusReceiver.InferTopLevelEntity):
        // a queue receiver's sending path is empty or equals its receiver path (the queue IS the top-level
        // entity); a topic subscription's sending path is the distinct topic (the TOPIC is the top-level
        // entity). Used to derive the top-level component of the per-receiver session key from the receiver's
        // sending/receiver paths at lookup time.
        private static string InferTopLevelEntity(string sendingPath, string messageReceiverPath)
        {
            if (string.IsNullOrWhiteSpace(sendingPath) || string.Equals(sendingPath, messageReceiverPath, StringComparison.Ordinal))
            {
                return messageReceiverPath;
            }

            return sendingPath;
        }

        private readonly struct RegisteredReceiver
        {
            public RegisteredReceiver(string topLevelEntity, string messageReceiverPath, TransactionMode? transactionMode, bool requiresSession)
            {
                TopLevelEntity = topLevelEntity;
                MessageReceiverPath = messageReceiverPath;
                TransactionMode = transactionMode;
                RequiresSession = requiresSession;
            }

            public string TopLevelEntity { get; }
            public string MessageReceiverPath { get; }
            public TransactionMode? TransactionMode { get; }
            public bool RequiresSession { get; }
        }
    }
}
