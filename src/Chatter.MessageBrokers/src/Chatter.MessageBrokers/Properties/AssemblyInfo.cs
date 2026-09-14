using System.Runtime.CompilerServices;

// Mirrors the Chatter.CQRS test-characterization precedent (CommandDispatcher.cs, EventDispatcher.cs, QueryDispatcher.cs).
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.AzureServiceBus")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.AzureServiceBus.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.SqlServiceBroker")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.SqlServiceBroker.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Reliability.Cosmos")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Reliability.Cosmos.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Reliability.EntityFramework")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.RabbitMQ")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.RabbitMQ.Tests")]
[assembly: InternalsVisibleTo("Chatter.Testing.Core")]
