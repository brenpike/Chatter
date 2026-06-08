using System.Runtime.CompilerServices;

// Mirrors the Chatter.CQRS test-characterization precedent (CommandDispatcher.cs, EventDispatcher.cs, QueryDispatcher.cs).
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.AzureServiceBus.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.SqlServiceBroker")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.SqlServiceBroker.Tests")]
[assembly: InternalsVisibleTo("Chatter.Testing.Core")]
