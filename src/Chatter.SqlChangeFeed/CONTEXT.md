# Chatter.SqlChangeFeed

Table-change notifications sourced from SQL Server, surfaced as a change feed (a.k.a. Table Watcher).

## Language

**Change Feed**: A stream of notifications emitted when rows in a watched SQL table change.
_Avoid_: change stream.

**Table Watcher**: The component that subscribes to changes on a specific table and raises change-feed notifications (original library name).

**Trigger**: SQL trigger installed on the watched table that captures inserts/updates/deletes.

**Stored Procedure**: Installed SQL procedure that the change-feed plumbing invokes to read/forward changes.

**Change Feed Options**: Configuration naming the watched table, database, connection, change types to watch, and feed behavior.

**Row Changed Event**: The default strongly-typed notifications fanned out per change — `RowInsertedEvent<T>`, `RowUpdatedEvent<T>`, `RowDeletedEvent<T>` — handled via `IMessageHandler<T>`.

**Change Feed Item**: A single captured row change (`ChangeFeedItem<T>`); a batch is delivered as `ProcessChangeFeedCommand<T>` in manual mode (`ProcessTableChangesManually()`).

**Change Feed Migration**: Opt-in, re-runnable SQL provisioning (`UseChangeFeedSqlMigrationsAsync<T>`) that installs the Service Broker objects, table Trigger, and install/uninstall Stored Procedures. A synchronous `UseChangeFeedSqlMigrations<T>` overload also exists but is deprecated (`[Obsolete]`, warning-level). Not run automatically at registration. Re-running it reconciles rather than repeats: the Stored Procedures are replaced in place, and the Trigger is refreshed only when the watched table's column fingerprint differs from the one the installed Trigger carries. It refuses a run outright — before creating or altering any Service Broker object — when the installed Service Broker topology diverges from the configured names, that is, when a service is bound to a queue this configuration does not use. The refusal is non-destructive and repairs nothing: recovery is to run the installed uninstall Stored Procedure for that change feed, then re-run the Change Feed Migration.

## Relationships

- A Change Feed Migration installs the Trigger and Stored Procedures on the watched table; provisioning is opt-in, not automatic.
- Re-running a Change Feed Migration is the repair path for a watched table whose columns changed after install: the Trigger is refreshed from the current column set, and left untouched when the columns are unchanged.
- The Trigger fires on row changes and pushes onto SQL Service Broker; the resulting notifications form the Change Feed.
- By default the Change Feed fans out to Row Changed Events; manual mode delivers raw Change Feed Items instead.
- Change-feed notifications are handled through Chatter.CQRS handlers and can be relayed via the Message Brokers context.

## Example dialogue

> **Dev:** "How does Chatter know a row changed without polling?"
> **Domain expert:** "Run the Change Feed Migration once to install the Trigger; it pushes changes onto SQL Service Broker, and the Change Feed delivers a RowInsertedEvent / RowUpdatedEvent / RowDeletedEvent to your handler."

## Flagged ambiguities

- **Table Watcher vs SQL Change Feed**: legacy code anchor was `chatter-tablewatcher`; the package is `Chatter.SqlChangeFeed`. Treat Table Watcher as the legacy alias.
