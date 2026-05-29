using System.Runtime.CompilerServices;

// Mirrors the Chatter.CQRS / Chatter.MessageBrokers characterization-test precedent (PR #107 / #108):
// exposes internals to the test assembly so behavior can be pinned without changing production logic.
[assembly: InternalsVisibleTo("Chatter.SqlChangeFeed.Tests")]
[assembly: InternalsVisibleTo("Chatter.Testing.Core")]
