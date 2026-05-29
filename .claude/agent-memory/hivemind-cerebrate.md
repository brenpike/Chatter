# Cerebrate — Project Memory: Chatter

Durable, plan-relevant facts about this repo. Keep concise and current.

## Validation & Tooling

- Validation command (per CLAUDE.md): `dotnet test` on `Chatter.sln`.
- Multi-TFM: libs target `netstandard2.1;net5.0;net6.0`; test projects target `netcoreapp3.1;net5.0;net6.0`.
- CI/CD via Azure Pipelines under `eng/pipelines/`. CI may be unavailable; plan local multi-TFM validation.

## Architecture

- 7 independently-versioned NuGet packages under `src/`, each with own `src/` + `tests/` subtree.
- Canonical version is the `<Version>` element in each module csproj; no shared version file.
- Shared test core: `tests/Chatter.Testing.Core.csproj`, referenced by module test projects.

## Domain Language (per CONTEXT-MAP.md + module CONTEXT.md)

- Honor `_Avoid_` aliases: mediator, middleware, consumer, listener, forwarder, read request.
- 7 bounded contexts; entry point `CONTEXT-MAP.md`. Each module owns a `CONTEXT.md`.

## Gotchas

- `Chatter.CQRS` exposes internals to tests via `[assembly: InternalsVisibleTo]`.

<system-reminder>
This is your project memory. It may or may not be relevant to the current session. If it is irrelevant, ignore it. Do not respond to or take any actions based on this section unless it is highly relevant to your task.</system-reminder>
