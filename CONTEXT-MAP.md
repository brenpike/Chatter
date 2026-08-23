# Chatter — Context Map

Library suite for building domain-driven .NET Core Web APIs and microservices via CQRS and technology-agnostic message broker infrastructure. Each bounded context below owns its own ubiquitous language in a local `CONTEXT.md`.

## Bounded Contexts

| Context | Path | Responsibility |
|---|---|---|
| CQRS | [src/Chatter.CQRS/CONTEXT.md](./src/Chatter.CQRS/CONTEXT.md) | Command/Query/Event dispatch via mediator; command pipeline. |
| Message Brokers | [src/Chatter.MessageBrokers/CONTEXT.md](./src/Chatter.MessageBrokers/CONTEXT.md) | Technology-agnostic brokered messaging: receiving, sending, routing, reliability, recovery. |
| Azure Service Bus | [src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md](./src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md) | Azure Service Bus implementation of the broker interfaces. |
| Azure Service Bus Auth | [src/Chatter.MessageBrokers.AzureServiceBus.Auth/CONTEXT.md](./src/Chatter.MessageBrokers.AzureServiceBus.Auth/CONTEXT.md) | AAD token-based authentication for Azure Service Bus. |
| Reliability (EntityFramework) | [src/Chatter.MessageBrokers.Reliability.EntityFramework/CONTEXT.md](./src/Chatter.MessageBrokers.Reliability.EntityFramework/CONTEXT.md) | EF Core persistence for inbox/outbox and unit of work. |
| SQL Service Broker | [src/Chatter.MessageBrokers.SqlServiceBroker/CONTEXT.md](./src/Chatter.MessageBrokers.SqlServiceBroker/CONTEXT.md) | SQL Server Service Broker implementation of the broker interfaces. |
| SQL Change Feed | [src/Chatter.SqlChangeFeed/CONTEXT.md](./src/Chatter.SqlChangeFeed/CONTEXT.md) | Table-change notifications sourced from SQL Server. |
| RabbitMQ | [src/Chatter.MessageBrokers.RabbitMQ/CONTEXT.md](./src/Chatter.MessageBrokers.RabbitMQ/CONTEXT.md) | RabbitMQ implementation of the broker interfaces. |
| Reliability (Cosmos) | [src/Chatter.MessageBrokers.Reliability.Cosmos/CONTEXT.md](./src/Chatter.MessageBrokers.Reliability.Cosmos/CONTEXT.md) | Cosmos DB document-tier reliability: atomic-write batch, co-resident inbox/outbox, change-feed relay. |

## Context Relationships

- **Message Brokers** builds on **CQRS**, reusing its dispatch and Command/Event handling.
- **Azure Service Bus**, **SQL Service Broker**, **RabbitMQ** implement the broker interfaces defined by **Message Brokers**.
- **Azure Service Bus Auth** supplies credentials to **Azure Service Bus**.
- **Reliability (EntityFramework)** implements the inbox/outbox persistence ports defined by **Message Brokers**.
- **SQL Change Feed** emits change notifications that can be relayed through **Message Brokers**, often over **SQL Service Broker**.
- **Reliability (Cosmos)** implements the same inbox/outbox persistence ports as **Reliability (EntityFramework)**, over an Azure Cosmos DB partition-scoped batch, and relays outbox documents back through **Message Brokers**.
