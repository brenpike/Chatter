# <a name="chatter-reliability-cosmos"></a> Chatter.MessageBrokers.Reliability.Cosmos

Document-tier (NoSQL) reliability for [Chatter.MessageBrokers](#chatter-messagebrokers), backed by Azure Cosmos DB.

> **Status — #218 skeleton.** This release ships the provider **skeleton** only: the package, DI surface, document-tier reliability surface (atomic-write handle), partition-key resolver, and the Document-Tier Batch-Lifecycle Behavior **shell**. The outbox enqueue behavior, inbox dedup marker, and the change-feed relay are **not yet implemented** — they arrive in #219 (outbox), #220 (inbox), and #222 (relay). The behavior shell opens and (when ops are staged) executes a `TransactionalBatch`, but contributes no outbox or inbox ops of its own yet.

## Overview

`Chatter.MessageBrokers.Reliability.Cosmos` is the document-tier (NoSQL) implementation of the reliability ports defined by [Chatter.MessageBrokers](#chatter-messagebrokers). Where the relational EntityFramework tier wraps the handler in an ambient transaction, the document tier uses a **stage-then-commit** model: the framework opens a Cosmos `TransactionalBatch`, the handler contributes its own aggregate writes, and the framework executes the batch once as the single commit point.

The two tiers share **only** the abstract enqueue / inbox / message contracts — not the EF-shaped `TransactionContext` / `InboxBehavior` mechanics. The document tier carries its document-store primitives (resolved partition key, bound container, batch handle, ETag) on its own provider-shaped **document-tier reliability surface**.

See [ADR-0006](../../docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md) and [ADR-0007](../../docs/adr/0007-cosmos-outbox-co-resident-change-feed-relay.md) for the design.

## Installation

```sh
dotnet add package Chatter.MessageBrokers.Reliability.Cosmos
```

The package targets `net8.0` and `net10.0` and pulls in `Microsoft.Azure.Cosmos` (SDK v3) for both frameworks.

## Getting Started

Registration happens against the **command pipeline builder**, which Chatter exposes through the `pipeline` action on `AddChatterCqrs(...)`. The provider **creates no container** — your application injects its document (aggregate) container and the change-feed lease container, plus a partition-key resolver and the container's partition-key path.

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Your application owns the CosmosClient and the containers.
    var cosmosClient = new CosmosClient(Configuration.GetConnectionString("Cosmos"));
    Container documentContainer = cosmosClient.GetContainer("app-db", "aggregates");
    Container leaseContainer = cosmosClient.GetContainer("app-db", "leases");

    services.AddChatterCqrs(Configuration, pipeline =>
            {
                pipeline.WithCosmosDocumentReliability(
                    documentContainer,
                    leaseContainer,
                    // Map the inbound message to its aggregate partition key.
                    inbound => new PartitionKey(inbound.CorrelationId),
                    // The container's partition-key path.
                    "/tenantId");
            })
            .AddMessageBrokers(/* message broker options */);
}
```

A factory overload accepts `Func<IServiceProvider, Container>` for both containers when they are resolved from the service provider.

The Document-Tier Batch-Lifecycle Behavior is registered as the **outermost** pipeline behavior. It resolves the partition key, opens the `TransactionalBatch`, exposes the atomic-write handle on the document-tier reliability surface for the duration of the handler, and then executes the batch once — **only if** an op was staged (an empty batch never calls the Cosmos transport).

### Prerequisites

- **Container injection.** The application owns and supplies both containers; the provider never creates one.
- **Partition-key resolver.** The resolver is application-supplied — only the application knows how a message maps to its aggregate partition. The single-partition constraint applies: a message must map to exactly one aggregate partition.
- **Container TTL enabled (for delivered-doc purge).** Post-delivery TTL purge of outbox documents (coming in #222) requires the application's container to have TTL enabled (`defaultTtl` set, e.g. `-1`). This is a documented application prerequisite, not automatic.

## Domain Language

See [CONTEXT.md](../../Chatter.MessageBrokers/CONTEXT.md) for the domain glossary (Document Tier, Document-Tier Batch-Lifecycle Behavior, Atomic-Write Handle, Partition-Key Resolver, Co-Resident Outbox / Inbox Marker, Outbox Relay).

[← All Chatter modules](../../../README.md)
