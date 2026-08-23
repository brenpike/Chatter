# Chatter

A suite of modular .NET libraries for building domain-driven Web APIs and microservices, pairing an in-process CQRS + mediator core with technology-agnostic message broker infrastructure.

## Validation

Hivemind's Validation Procedure runs the command in this block.

```
dotnet test
```

Solution is `Chatter.sln`; tests live under `src/<Module>/tests/*.Tests.csproj` plus shared `tests/Chatter.Testing.Core.csproj`; library modules target `net8.0;net10.0` and the shared test core and test projects target `net8.0;net10.0`.

## Project Layout

- `src/` — 9 independently-versioned NuGet package modules, each with its own `src/` and `tests/` subtree.
- `tests/` — shared test core (`Chatter.Testing.Core.csproj`) referenced by all module test projects.
- `.github/workflows/` — GitHub Actions CI/CD: one `<module>-cicd.yml` per module plus shared/reusable workflows (version checks, tagging, CodeQL, top-level CI, scheduled integration runs).
- `CONTEXT-MAP.md` — entry point to bounded-context docs; lists all 9 contexts, their paths, and relationships.

## Domain Language

Each bounded context owns its ubiquitous language in a local `CONTEXT.md`; start at `CONTEXT-MAP.md`. When introducing a domain term, prefer the existing word from the relevant `CONTEXT.md`; honor `_Avoid_:` aliases (e.g. mediator, middleware, consumer, listener, forwarder, read request).

- `CQRS` → `./src/Chatter.CQRS/CONTEXT.md`
- `Message Brokers` → `./src/Chatter.MessageBrokers/CONTEXT.md`
- `Azure Service Bus` → `./src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md`
- `Azure Service Bus Auth` → `./src/Chatter.MessageBrokers.AzureServiceBus.Auth/CONTEXT.md`
- `Reliability (EntityFramework)` → `./src/Chatter.MessageBrokers.Reliability.EntityFramework/CONTEXT.md`
- `SQL Service Broker` → `./src/Chatter.MessageBrokers.SqlServiceBroker/CONTEXT.md`
- `SQL Change Feed` → `./src/Chatter.SqlChangeFeed/CONTEXT.md`
- `RabbitMQ` → `./src/Chatter.MessageBrokers.RabbitMQ/CONTEXT.md`
- `Reliability (Cosmos)` → `./src/Chatter.MessageBrokers.Reliability.Cosmos/CONTEXT.md`

## Conventions Worth Pinning

- xUnit + FluentAssertions + Moq + coverlet; each module has its own `*.Tests.csproj` and references `tests/Chatter.Testing.Core.csproj`.
- `Chatter.MessageBrokers.SqlServiceBroker` does NOT auto-provision Service Broker objects — queues/services/contracts/`ENABLE_BROKER` are set up manually (README §SqlServiceBroker).
- `Chatter.MessageBrokers.RabbitMQ` provisions NO topology — exchanges, queues, bindings, and the DLX are created externally.
- `Chatter.MessageBrokers.Reliability.EntityFramework` ships `IEntityTypeConfiguration` types meant to be applied inside the consumer's `DbContext.OnModelCreating` (README §Reliability).
- `IExternalDispatcher` is a no-op by default; a broker module replaces it (`./src/Chatter.CQRS/CONTEXT.md`).
- `Forwarder` is a specialization of `Router` (`ForwardingRouter` / `IBrokeredMessageForwarder` overlap — `./src/Chatter.MessageBrokers/CONTEXT.md`).

## Workflow & Agents

Branch, commit, PR, review, version-bump, agent dispatch, model routing, and skill selection are governed by the [hivemind plugin](https://github.com/brenpike/hivemind) (see `.claude/settings.json` → `enabledPlugins.hivemind@brenpike` and `agent: hivemind:overlord`). Do not restate or override those rules here.

## Versioning

The 9 independently-versioned NuGet packages are: `Chatter.CQRS`, `Chatter.MessageBrokers`, `Chatter.MessageBrokers.AzureServiceBus`, `Chatter.MessageBrokers.AzureServiceBus.Auth`, `Chatter.MessageBrokers.Reliability.EntityFramework`, `Chatter.MessageBrokers.SqlServiceBroker`, `Chatter.SqlChangeFeed`, `Chatter.MessageBrokers.RabbitMQ`, and `Chatter.MessageBrokers.Reliability.Cosmos`. Each package's canonical version is the `<Version>` element in its own csproj: `src/Chatter.CQRS/src/Chatter.CQRS/Chatter.CQRS.csproj`, `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Chatter.MessageBrokers.csproj`, `src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/Chatter.MessageBrokers.AzureServiceBus.csproj`, `src/Chatter.MessageBrokers.AzureServiceBus.Auth/src/Chatter.MessageBrokers.AzureServiceBus.Auth/Chatter.MessageBrokers.AzureServiceBus.Auth.csproj`, `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/Chatter.MessageBrokers.Reliability.EntityFramework.csproj`, `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Chatter.MessageBrokers.SqlServiceBroker.csproj`, `src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/Chatter.SqlChangeFeed.csproj`, `src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/Chatter.MessageBrokers.RabbitMQ.csproj`, `src/Chatter.MessageBrokers.Reliability.Cosmos/src/Chatter.MessageBrokers.Reliability.Cosmos/Chatter.MessageBrokers.Reliability.Cosmos.csproj`. No shared version file (`Directory.Build.props`, `version.props`, `version.json`) exists. Bump-trigger logic and dominant-row precedence defer to hivemind `governance/versioning.md`.

## Out of Scope (owned by hivemind)

- branch taxonomy / naming / one-plan-one-branch-one-PR — `plugin/governance/workflow.md`
- conventional-commit types — `plugin/governance/workflow.md`
- trunk-freshness check — `plugin/governance/workflow.md`
- PR opening conditions and required PR content — `plugin/governance/workflow.md`
- brood execution / strain decomposition — `plugin/governance/workflow.md`
- SemVer bump-trigger logic and dominant-row precedence — `plugin/governance/versioning.md`
- CHANGELOG / Keep-a-Changelog format and reset — `plugin/governance/versioning.md`
- agent roster (cerebrate, drone, changeling, overlord, local-reviewer, github-reviewer) — `plugin/agents/*`
- model routing per agent — overlord/agent frontmatter
- skill catalog — plugin marketplace
- validation procedure semantics (what "passed" means, retry rules) — overlord docs
- security policy / safety rails — `plugin/governance/{security-policy,safety-rails}.md`
- report format — `plugin/governance/report-format.md`
