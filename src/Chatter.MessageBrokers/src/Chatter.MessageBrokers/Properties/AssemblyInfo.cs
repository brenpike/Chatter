using System.Runtime.CompilerServices;

// Mirrors the Chatter.CQRS test-characterization precedent (CommandDispatcher.cs, EventDispatcher.cs, QueryDispatcher.cs).
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Tests")]
[assembly: InternalsVisibleTo("Chatter.Testing.Core")]
