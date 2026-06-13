---
name: rabbitmq-di-fold
description: How the RabbitMQ adapter folds into DI (AddRabbitMq), its one lifetime divergence, and where FullAtomicity is rejected
metadata:
  type: project
---

The RabbitMQ adapter's `AddRabbitMq(this IChatterBuilder, Action<RabbitMqOptionsBuilder>)` mirrors SSB's `AddSqlServiceBroker` fold exactly, with ONE deliberate divergence and a startup guard.

**Why:** STEP-006 of the RabbitMQ adapter initiative; the fold must match the established SSB/ASB `MessagingInfrastructureFactory(() => receiver, () => dispatcher)` pattern keyed by `RabbitMqMessageContext.InfrastructureType`.

**How to apply:**
- `IRabbitMqConnectionSource -> RabbitMqConnectionSource` is **Singleton** (one IConnection/process) — the ONLY lifetime divergence from SSB (whose `ISqlConnectionSource` is Scoped). Everything else mirrors SSB: receiver/sender Scoped, predicate providers Singleton, body converter Scoped, options Singleton-instance.
- Path builder: RabbitMQ uses the 4-arg `MessagingInfrastructure` ctor (like ASB, not the 3-arg SSB uses) to inject `RabbitMqPathBuilder` (registered as concrete Singleton). `RabbitMqPathBuilder` is the identity mapping (default-exchange: path == receiver/queue name); the 3-member `IBrokeredMessagePathBuilder` has NO error/deadletter method — those names live on `ReceiverOptions` and are resolved by the receiver's republish, not this seam.
- `FullAtomicityViaInfrastructure` is rejected at REGISTRATION (throws `NotSupportedException`), not first-send. The guard reads the global `MessageBrokerOptions.TransactionMode` AND per-receiver modes off `IDiscoveredReceiverRegistry`, both read as singleton `ImplementationInstance` directly off `IServiceCollection` (no provider build) — same technique ASB uses for its cross-entity startup guard. RabbitMQ-attribution mirrors ASB: claim explicit-RabbitMq receivers always; claim blank-typed only when RabbitMQ is the core default (no `IMessagingInfrastructure` registered yet when AddRabbitMq runs). See [[materializer-cross-assembly-visibility]] sibling adapters.
- `MessageHandlerContextExtensions.RabbitMq()` was left as-is (writes InfrastructureType directly == `InfrastructureTypes.RabbitMq()`, behavior-neutral; re-pointing would need an `InfrastructureTypes` instance, not trivial).
