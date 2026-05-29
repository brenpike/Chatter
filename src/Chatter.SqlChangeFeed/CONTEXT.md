# Chatter.SqlChangeFeed

Table-change notifications sourced from SQL Server, surfaced as a change feed (a.k.a. Table Watcher).

## Language

**Change Feed**: A stream of notifications emitted when rows in a watched SQL table change.
_Avoid_: change stream.

**Table Watcher**: The component that subscribes to changes on a specific table and raises change-feed notifications (original library name).

**Trigger**: SQL trigger installed on the watched table that captures inserts/updates/deletes.

**Stored Procedure**: Installed SQL procedure that the change-feed plumbing invokes to read/forward changes.

**Change Feed Options**: Configuration naming the watched table, connection, and feed behavior.

## Relationships

- A Table Watcher installs Triggers and Stored Procedures on the watched table via Setup Scripts.
- Triggers fire on row changes; the resulting notifications form the Change Feed.
- Change-feed notifications can be relayed through the Message Brokers context, typically over SQL Service Broker.

## Example dialogue

> **Dev:** "How does Chatter know a row changed without polling?"
> **Domain expert:** "The Table Watcher installs Triggers that push onto SQL Service Broker; the Change Feed delivers those notifications to your handler."

## Flagged ambiguities

- **Table Watcher vs SQL Change Feed**: code anchors use `chatter-tablewatcher`; the package is `Chatter.SqlChangeFeed`. Treat Table Watcher as the legacy alias.
