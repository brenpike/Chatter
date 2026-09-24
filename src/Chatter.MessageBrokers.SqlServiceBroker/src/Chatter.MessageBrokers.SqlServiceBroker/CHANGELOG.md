# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.15.0] - 2026-09-24

### Changed

- **A missing or misnamed queue now stops the receiver instead of retrying forever.** SQL errors 208 (invalid object name)
  and 102 (syntax error) are now classified terminal rather than transient: both the retry and circuit-breaker exception
  predicate providers exclude them, and the receiver surfaces them as `CriticalReceiverException` naming the configured
  queue. **This is a breaking change**: a host started before its queue exists used to wait and recover when the queue
  appeared; it now stops and must be restarted once the queue exists. Because the predicate providers are registered
  globally, error 208 raised anywhere under Chatter's recovery pipeline (for example, a handler hitting a table that a
  concurrent migration has not yet created) is no longer retried. (#358)
- **Service Broker `Error` messages are logged at `Error`, with their payload decoded**, instead of at `Trace`. The
  conversation handle, the service name, and the Service Broker error code and description are each logged as their
  own structured field. The error code is parsed as a 32-bit integer and logged as that integer re-rendered — never
  as the peer's own text for it. The description is text the dialog peer chose: only printable letters, marks,
  numbers, punctuation, symbols and spaces are logged from it, anything else becomes a space, and it is capped at
  3000 characters. A body that is absent or empty logs `<no error payload>`; a `<Code>` that is not a 32-bit integer —
  including one produced by invalid UTF-16 or an otherwise unreadable payload — logs `<unreadable error payload>`
  in place of both the code and the description. Other discarded messages stay at `Trace`. (#357)

### Fixed

- **A discarded message's conversation is now ended, so errored conversation endpoints no longer accumulate in
  `sys.conversation_endpoints`.** Every discard of a received message (a Service Broker `Error`, a message of a type the
  receiver does not accept, or a Chatter message with no body) now issues `END CONVERSATION` before the RECEIVE
  commits — on the receive transaction itself under a transactional mode, so the two commit together; under
  `TransactionMode.None` there is no receive transaction and each statement autocommits on its own, the same way ack,
  nack and deadletter already behave under that mode. Previously the RECEIVE committed and the endpoint was left open
  (or in the error state) indefinitely. **This is a breaking change** for a deployment where a non-Chatter application shares the queue and
  keeps long-lived multi-message dialogs: such a dialog is now ended when Chatter discards one of its messages
  (previously that message was silently dropped). (#357)

No public API or configuration option changed; both decisions are internal to the receiver and its recovery-pipeline
predicate providers. See `docs/adr/0037-a-terminal-receive-outcome-ends-its-conversation-and-a-deterministic-sql-fault-is-not-retried.md`
for the rationale.

## [0.14.5] - 2026-09-23

### Changed

- **A comment on `SqlServiceBrokerReceiver` is corrected to name the kind-tested Message Context reads that `Chatter.MessageBrokers` 0.34.0 ships.** The comment beside the receiver's null-guard on the deserialized envelope's Message Context no longer says an upstream-stamped non-string header avoids an `InvalidCastException` on the downstream `GetMessageContextByKey<T>` casts — those reads no longer cast, so no such exception can arise; it now says the header is found by those kind-tested reads rather than read as absent. **This carries no code change and no user-facing effect: it corrects prose inside an internal type to match behaviour a sibling change already shipped, and satisfies no formal SemVer trigger.** It ships as its own release because this repository's version-check gate is path-based and cannot distinguish a comment edit from a behavioural one. See `docs/adr/0036-a-message-context-read-tests-the-persisted-kind-rather-than-casting-it.md` (#418).

## [0.14.4] - 2026-09-14

### Changed

- `JsonUnicodeBodyConverter` now serializes and deserializes through the public serialization methods on `Chatter.MessageBrokers` instead of reaching into that package's shared serializer configuration. This is an internal call-site change: the serialized output is byte-identical, the payload encoding is unchanged (this package still writes and reads its UTF-16 `application/json; charset=utf-16` wire format), and there is no consumer-visible behaviour change. (#302)
- Bundled dependency uplift to Chatter.MessageBrokers 0.31.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it). Install this release together with `Chatter.MessageBrokers` 0.31.0: that release makes the shared serializer configuration inaccessible to other packages, so a 0.14.3 or earlier build of this package composed against it by NuGet would fail at runtime. Republishing this package with a `Chatter.MessageBrokers` 0.31.0 floor is what prevents that composition.

## [0.14.3] - 2026-09-13

### Changed

- `AddSqlServiceBroker` registers `JsonUnicodeBodyConverter` as `Singleton` (previously `Scoped`), matching the process-lifetime `IBodyConverterFactory` in `Chatter.MessageBrokers`. Without it, a host with scope validation enabled (the default in the Development environment) fails at startup with `InvalidOperationException` ("Cannot consume scoped service ... from singleton 'IBodyConverterFactory'") once paired with that release. Upgrade together with `Chatter.MessageBrokers` 0.30.0. (#342)
- Bundled dependency uplift to Chatter.MessageBrokers 0.30.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [0.14.2] - 2026-09-12

### Changed

- The target service name passed to `BEGIN DIALOG` is no longer bracket-stripped unconditionally. It is now unquoted only when it is a well-formed quoted identifier, and otherwise passed through intact: `[Target]` still binds as `Target`; `my]service` now binds intact where it was previously corrupted to `myservice`; `[Target]Svc` now binds verbatim where it previously became `TargetSvc`. A configured target service name containing brackets will bind a different value than before.
- An initiator service name that is null, empty, or whitespace now throws `ArgumentException`, where it previously emitted `FROM SERVICE []`. Because the constructor defaults the initiator to the target when no initiator is supplied, this also fires for a null or blank target.
- A null target service name supplied alongside an explicit initiator now binds a null `@targetService` parameter instead of throwing `NullReferenceException`.
- A queue name with an empty part (e.g. `dbo.`, `.MyQueue`) now throws `ArgumentException` at command-build time instead of emitting broken SQL.
- A dotted queue name is now read as `schema.queue`, with each part quoted separately. A one-part queue name that legitimately contains a dot must be pre-bracketed in configuration (`[my.queue]`); an already-bracketed name is accepted verbatim, so existing pre-bracketed configuration is unaffected.
- `BeginDialogConversationCommand.Create()` no longer mutates the instance's public `_targetServiceName` field, so a caller reading that field after `Create()` no longer sees a rewritten value.

### Fixed

- `UseConversationEncryption()` was a silent no-op: the option never reached `BEGIN DIALOG`, so every dialog was begun with `WITH ENCRYPTION = OFF` regardless of configuration. It now takes effect. **An application that had already called `UseConversationEncryption()` was silently getting unencrypted dialogs and will now genuinely require dialog security provisioned server-side.** (#356)

### Security

- Two T-SQL identifier positions that were built by raw string interpolation are now bracket-quoted: the `RECEIVE` queue name (`ReceiveMessageFromQueueCommand`) and the `BEGIN DIALOG` initiator service name (`BeginDialogConversationCommand`). A name containing `]` could previously terminate the identifier early and append arbitrary T-SQL, on a connection that typically holds `RECEIVE`/`SEND` rights on the target database. Value positions (`TO SERVICE`, `ON CONTRACT`) were never affected — they were already bound as `SqlParameter`s. See `docs/adr/0016-sql-identifier-quoting-via-round-trip-parse.md` for the quoting rule. (#355)

## [0.14.1] - 2026-09-02

### Changed

- Bundled dependency uplift to Chatter.MessageBrokers 0.18.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [0.14.0] - 2026-09-01

### Added

- Published packages now ship a symbol package (`.snupkg`), an embedded `README`, a project URL, and are built deterministically. Package builds are now reproducible CI builds with SourceLink-resolvable sources, so a debugger can step into this package's original source from a consuming application.

### Changed

- Raised the net8.0-leg `Microsoft.Extensions.Hosting` dependency floor to `8.0.1`, off a dependency graph that carried an advisory-affected `System.Text.Json` 8.0.0 floor. The net10.0 leg is unchanged.
- Bundled dependency uplift to Chatter.MessageBrokers 0.17.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [0.13.0] - 2026-08-30

### Changed

- **BREAKING:** `SqlServiceBrokerReceiver`'s settlement members (`AckMessageAsync`, `NackMessageAsync`, `DeadletterMessageAsync`) now return `Task<SettlementResult>` instead of `Task<bool>`, in step with the `Chatter.MessageBrokers` 0.16.0 seam. A custom caller depending on the prior `bool` return must migrate to read `SettlementResult.IsSettled`/`.Outcome`. `DeadletterMessageAsync` with no received message carried in the context now returns a `Failed` outcome instead of throwing `ArgumentException` — Recovery no longer retries this deterministic fault, since retrying it could not succeed (#283).

### Fixed

- Receive-side SQL transaction now honors the per-receiver `ReceiverOptions.TransactionMode` (set via `AddQueueReceiver<T>(transactionMode:)`) instead of always falling back to the global `MessageBrokerOptions.TransactionMode` — two receivers configured with different modes both previously used the global mode. (#235)

## [0.12.1] - 2026-06-14

### Fixed

- Enable `MultipleActiveResultSets` on the Service Broker connection — `Microsoft.Data.SqlClient` enforces MARS, which the `RECEIVE`-then-settle dialog pattern requires (the receiver otherwise threw `InvalidOperationException: The connection does not support MultipleActiveResultSets`).
- Fix `EndDialogConversationCommand` to await its command execution — it previously returned an un-awaited Task while disposing the `SqlCommand`, which `Microsoft.Data.SqlClient` rejects with `EndExecuteNonQuery cannot be called more than once`.
- Receive-path transaction lifecycle: route every receive outcome (empty-RECEIVE discard, end-dialog, ack, nack, deadletter) through a single guarded settle so each connection/transaction is committed-or-rolled-back-or-disposed exactly once — fixes `InvalidOperationException: This SqlTransaction has completed; it is no longer usable` (and an `await null` NRE under TransactionMode.None) surfaced by the Microsoft.Data.SqlClient migration.
- Decode the inbound typed payload with the converter for the inner body's own content type (carried in the envelope's `ContentType` header, `application/json`/UTF-8 by default) instead of reusing the UTF-16 envelope converter — the inner DTO is encoded UTF-8 by the core dispatcher, so reusing the UTF-16 `JsonUnicodeBodyConverter` mis-decoded it and threw `PoisonedMessageException` (`'0xE2' is an invalid start of a value`) on every round-trip. The UTF-16 envelope wire format is unchanged (non-breaking).

## [0.12.0] - 2026-06-14

### Changed

- **BREAKING:** Migrated from the deprecated `System.Data.SqlClient` to `Microsoft.Data.SqlClient` (7.0.1). The public API now exposes `Microsoft.Data.SqlClient` types — the `Scripts` command types (e.g. `BeginDialogConversationCommand`) and any `SqlConnection`/`SqlTransaction` passed via the message `TransactionContext`. Consumers compiled against `System.Data.SqlClient` that construct these types or stow a `System.Data.SqlClient.SqlTransaction` in the transaction context must migrate to `Microsoft.Data.SqlClient`. Microsoft.Data.SqlClient defaults `Encrypt=true` with server-certificate validation (the legacy provider did not). Connection strings targeting a self-signed/untrusted-certificate server must now set `Encrypt=False` or `TrustServerCertificate=True` explicitly, or `Open`/`OpenAsync` will fail. Fixes the nightly SSB integration `ReflectionTypeLoadException` (SqlGuidCaster) on net8/net10. (#204)

## [0.11.1] - 2026-06-12

### Fixed

- Renamed the SQL parameter `@timeoutInSeconds` to `@timeoutInMilliseconds` in `ReceiveMessageFromQueueCommand` to reflect that the value passed to `WAITFOR ... TIMEOUT` is in milliseconds, not seconds. Pure rename; no change to wait duration or behavior. (#181)

## [0.11.0] - 2026-06-07

### Changed

- **BREAKING:** `JsonUnicodeBodyConverter` (UTF-16) now deserializes message DTOs with System.Text.Json. Message DTOs annotated with Newtonsoft contract attributes — `[JsonProperty("name")]`, `[JsonIgnore]`, `[JsonConverter]`, etc. — are no longer honored. Migrate such DTOs to the System.Text.Json equivalents (`[JsonPropertyName("name")]`, `[System.Text.Json.Serialization.JsonIgnore]`, STJ `[JsonConverter]`). Property-name aliasing and member-ignore contracts must be re-expressed with STJ attributes or wire compatibility for those specific contracts will break. (Default Newtonsoft read/write behavior is preserved for un-annotated DTOs; only explicit Newtonsoft attribute contracts require migration.)
- Ported JsonUnicodeBodyConverter (UTF-16) to System.Text.Json via the shared Chatter.MessageBrokers serializer options; wire format unchanged. Dropped the transitive Newtonsoft.Json dependency.

### Fixed

- SQL Service Broker inbound headers now materialize System.Text.Json-deserialized context values to their CLR types so downstream typed reads no longer throw InvalidCastException; removed two vestigial Guid casts in the sender that could throw on round-tripped values.

## [0.10.1] - 2026-06-07

### Changed

- Rerouted DI registration to the shared core `MessagingInfrastructureFactory`; removed the internal `SqlServiceBrokerInfrastructureFactory` (behavior-preserving — identical scope-open/resolve/dispose semantics). Now depends on `Chatter.MessageBrokers` >= 0.10.0.

## [0.10.0] - 2026-06-07

### Changed

- `IMessageHandlerContext.SqlServiceBroker()` now returns the core `IMessageBrokerContext` (was `ISqlServiceBrokerContextDispatcher`). The same `Send`/`Publish`/`Forward` members remain, so callers using those recompile unchanged.

### Removed

- `SqlServiceBrokerContextDispatcher` and `ISqlServiceBrokerContextDispatcher` — pass-through wrappers collapsed into `IMessageBrokerContext`. Code referencing those types directly is broken.

## [0.9.0] - 2026-06-07

### Changed

- `SqlServiceBrokerReceiver` and `SqlServiceBrokerSender` are now `internal` (removed from the public API surface), thinned behind injectable SDK seams. Behaviour is unchanged.
- Connection creation now flows through an internal `ISqlConnectionSource` seam (production `SqlClientConnectionSource`), shared by the receiver and sender.
- Message classification extracted into a pure internal `ServiceBrokerMessageClassifier`; the sender's transaction-enlistment decision extracted into a pure internal `OutboundTransactionPolicy`.
- The separate `SqlServiceBrokerReceiverFactory` / `SqlServiceBrokerSenderFactory` are folded into a single `Func<>`-backed internal `SqlServiceBrokerInfrastructureFactory`.

### Removed

- `SqlServiceBrokerReceiverFactory` and `SqlServiceBrokerSenderFactory` (folded into `SqlServiceBrokerInfrastructureFactory`).

## [0.8.1] - 2026-06-06

### Fixed

- Upgraded `System.Data.SqlClient` 4.8.3 -> 4.8.6, resolving Dependabot alert #11 (SQL Data Provider Security Feature Bypass, high) and #1 (.NET Information Disclosure, medium).

## [0.8.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
