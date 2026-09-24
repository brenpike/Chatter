---
status: accepted
date: 2026-09-24
---

# Chatter single-targets net10.0 and drops net8.0 before its end-of-life date

All nine packages, the shared test core and every test project move from `net8.0;net10.0` to `net10.0` alone. Two
parts of that are contested and would otherwise be invisible in the diff: it lands before the date issue #395 itself
said to wait for, and it bumps the nine packages by different SemVer rules. This ADR records both.

Issue #395.

## Context

**.NET 8 reaches end of support on 2026-11-10.** After that date it receives no security patches, and a `net8.0`
target advertises support Chatter cannot deliver: a consumer who hits a runtime CVE has no upstream fix.

**There is no STS rung to step onto.** .NET 9 reaches end of support on the same day as .NET 8, so `net9.0` is not
an intermediate target worth adding. `net10.0` is the only supported option.

**The `net8.0` leg is where dependency rot concentrates.** #388 existed only because the `net8.0` legs pinned
`Microsoft.Extensions.*` 8.0.0, whose graph carried an advisory-affected `System.Text.Json` floor; the `net10.0` legs
were clean. #394 hits the same wall: Scrutor 7.0.0 takes an unconditional `Microsoft.Extensions.*` 10.0.0 floor, so
upgrading it lands a mixed 8.0/10.0 graph on the `net8.0` leg for as long as that leg exists.

**Single-targeting is a precondition for the Native AOT work** tracked by #275, which targets the .NET 10 toolchain.

**The integration lane runs both TFMs against one emulator** (#424), and lock-sensitive tests flake under that
contention. Single-targeting removes the cross-TFM axis of that contention. It does not remove contention in general:
tests within one TFM still share the emulator.

**What the case is NOT.** It is not source maintenance burden. The multi-targeting cost in source was 24 conditional
`ItemGroup`s, every one of them a dependency-version conditional, and four `#if NET9_0_OR_GREATER` blocks, all the
same shape: use the newer BCL API on `net10.0`, fall back on `net8.0`. That is cheap to carry. The case rests on the
support date and the dependency graph, not on code.

## Considered Options

### Option A — drop `net8.0` now, from all nine packages in one PR (ACCEPTED)

Recorded under *Decision*.

### Option B — wait until 2026-11-10, as #395 says (REJECTED)

Recorded under Decision 1: waiting protects no consumer, because none is stranded by landing early.

### Option C — drop `net8.0` package by package over several PRs (REJECTED)

Every package depends on `Chatter.CQRS` through its `ProjectReference` chain. A partial drop leaves a `net10.0`-only
package depending on one that still offers `net8.0`, so the suite publishes an inconsistent set of target frameworks
until the last PR lands, and the rot described above stays in the graph in the meantime.

## Decision

### 1. The "Do not start before 2026-11-10" gate in #395 is waived

#395 says not to start before .NET 8's end-of-support date. The owner has chosen to land this change before it.

The reason: consumers still on `net8.0` can stay on the current releases for the few remaining weeks. The last
multi-targeting release of every package stays installable on NuGet indefinitely, so no consumer is stranded. They
stop receiving new versions until they move to .NET 10, which they must do anyway once .NET 8 stops receiving
security patches.

### 2. Seven 0.x packages take a MINOR; the two post-1.0 packages take a MAJOR

Removing a target framework is a breaking change for any consumer on that framework. How that is expressed depends on
where the package is in SemVer:

| Package | From | To |
| --- | --- | --- |
| `Chatter.CQRS` | 0.17.0 | 0.18.0 |
| `Chatter.MessageBrokers` | 0.34.0 | 0.35.0 |
| `Chatter.MessageBrokers.Reliability.EntityFramework` | 0.11.0 | 0.12.0 |
| `Chatter.MessageBrokers.SqlServiceBroker` | 0.15.0 | 0.16.0 |
| `Chatter.SqlChangeFeed` | 0.14.2 | 0.15.0 |
| `Chatter.MessageBrokers.RabbitMQ` | 0.5.1 | 0.6.0 |
| `Chatter.MessageBrokers.Reliability.Cosmos` | 0.8.1 | 0.9.0 |
| `Chatter.MessageBrokers.AzureServiceBus` | 2.5.3 | 3.0.0 |
| `Chatter.MessageBrokers.AzureServiceBus.Auth` | 3.2.1 | 4.0.0 |

- **The seven 0.x packages take a MINOR**, with an explicit `### Removed` entry and the `**This is a breaking
  change**` marker in each changelog. That is how breaking changes in 0.x packages have shipped here before (#330,
  #445, #357).
- **The two post-1.0 packages take a genuine MAJOR.** SemVer leaves no other option once a package is past 1.0.
- **All nine ship in one PR**, for the reason recorded under Option C.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: a target framework advertising support that cannot be delivered.** The `net8.0` target is gone,
so no Chatter package can offer it after .NET 8 stops being patched.

**What it does NOT close is dependency rot in general.** One target framework has fewer legs to go stale, but a
`net10.0` dependency can fall behind or pick up an advisory the same way the `net8.0` legs did. #394 is still open
work; this change removes the mixed-floor complication from it, not the upgrade itself.

## Consequences

- **Runtime behaviour on `net10.0` is unchanged.** The code that runs on `net10.0` today is the code that runs after
  this change; only the `net8.0` branches are deleted.
- **The `net10.0` side of each `#if NET9_0_OR_GREATER` block becomes unconditional.** Histogram bucket boundary
  advice and `Activity.AddException` are now used on every build. ADR-0010 is amended to say so.
- **#392 is folded in.** Its always-true `#if NET5_0_OR_GREATER` directives are removed in the same change.
- **CI runs one leg.** Each workflow builds and tests `net10.0` only.
- **Consumers on `net8.0` stop receiving new versions.** The previous releases stay on NuGet (Decision 1).

## References

- Issue #395 — *Drop net8.0 and single-target net10.0 after .NET 8 EOL (2026-11-10)*.
- Issue #388 — *net8.0 legs pin Microsoft.Extensions 8.0.0, carrying a stale advisory-affected System.Text.Json
  floor*.
- Issue #394 — *Scrutor pinned at 3.3.0, four majors behind 7.0.0, in a shipped package*.
- Issue #275 — *Native AOT / trimming compatibility — tracking issue*.
- Issue #424 — *Integration lane runs both TFMs against one emulator, so lock-sensitive tests flake*.
- Issue #392 — *Remove five always-true #if NET5_0_OR_GREATER directives and their test mirrors*.
- ADR-0010 — *Optional BCL-only telemetry: per-assembly instrumentation scopes and the off-guard*. Its `net8.0`
  member-availability table and `net8.0` `ActivityOutcome` branch describe the leg this ADR removes.
- .NET blog — *.NET 8 and .NET 9 will reach End of Support on November 10, 2026* —
  <https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/>
