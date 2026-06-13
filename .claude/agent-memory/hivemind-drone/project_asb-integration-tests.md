---
name: project_asb-integration-tests
description: ASB integration-test scaffolding via Testcontainers Service Bus emulator — Docker-probe false-positive gotcha, emulator cross-entity-transaction uncertainty, and skip-clean design
metadata:
  type: project
---

Integration tests for `Chatter.MessageBrokers.AzureServiceBus` live in the EXISTING ASB test project under `src/Chatter.MessageBrokers.AzureServiceBus/tests/Integration/` (no separate project). They use the official Azure Service Bus emulator via `Testcontainers.ServiceBus` (4.12.0; auto-wires the required MSSQL sidecar). Tagged `[Trait("Category","Integration")]`; CI splits on `--filter Category!=Integration` (fast unit job) vs `--filter Category=Integration` (emulator job).

**Why:** STEP-008 of the ASB SDK migration. The new shared-client `EnableCrossEntityTransactions` wiring had NO test proving atomic commit/rollback against a real broker. See [[project_asb-sdk-migration]].

**How to apply (Docker-probe gotcha):** A plain `dotnet test` with no working Docker MUST report integration tests SKIPPED, never FAILED. The discovery-time skip is driven by `DockerEnvironment.IsAvailable` (custom `[RequiresDockerFact]`). CRITICAL: probing only that `/var/run/docker.sock` EXISTS and a socket `connect()` SUCCEEDS is a FALSE POSITIVE on WSL — the socket file is present and accepts connections even when no daemon serves it, so tests ran and HARD-FAILED with "Failed to connect to Docker endpoint". The fix: the probe issues a real Docker `GET /_ping` over the endpoint (HttpClient + SocketsHttpHandler unix-socket ConnectCallback) and only treats a success status as available. Do not regress to a bare socket-connect probe.

**How to apply (emulator cross-entity transactions — UNVERIFIED, possible blocker):** The official emulator docs (learn.microsoft.com/azure/service-bus-messaging/overview-emulator) do NOT list transactions or cross-entity transactions among known limitations (only: no JMS, no partitioned entities, AMQP-TCP only). But this could NOT be empirically confirmed (no working Docker in the authoring env). The atomic-commit and non-atomic tests are authored as live `[RequiresDockerFact]`; the atomic-ROLLBACK / cross-entity test is `[Fact(Skip=...)]` with a documented reason (authored ready-to-run) rather than faked green. When a working Docker IS available, RUN the rollback test against the emulator; if cross-entity rollback works, remove its Skip — if it does not, keep it skipped and the documented reason stands.

**Tests drive the SDK directly** (a single `EnableCrossEntityTransactions` `ServiceBusClient`, mirroring `ChatterAzureServiceBusExtensions.CreateSharedClient`), NOT the full Chatter pipeline — this isolates the broker-level guarantee from the pipeline's scope orchestration (unit-tested elsewhere). Queues `queue.a`/`queue.b` are provisioned from `Integration/Config.json` (namespace `sbemulatorns`, copied to output via `CopyToOutputDirectory=PreserveNewest`); the emulator does NOT auto-create entities.
