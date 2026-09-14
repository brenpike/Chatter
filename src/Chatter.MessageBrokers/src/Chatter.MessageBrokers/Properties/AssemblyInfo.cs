using System.Runtime.CompilerServices;

// TEST grants. Exposing internals to a package's own test assemblies is what InternalsVisibleTo is
// for: these mirror the Chatter.CQRS test-characterization precedent (CommandDispatcher.cs,
// EventDispatcher.cs, QueryDispatcher.cs) and are a sanctioned, permanent part of this assembly.
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.AzureServiceBus.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.SqlServiceBroker.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.RabbitMQ.Tests")]
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Reliability.Cosmos.Tests")]
[assembly: InternalsVisibleTo("Chatter.Testing.Core")]

// PRODUCTION grants. These are DEBT, not a sanctioned pattern: a shipped Chatter package should
// compose against this assembly's PUBLIC surface only, so that a version of it resolved by NuGet
// against a newer Chatter.MessageBrokers cannot break on an internal that moved. Each remaining
// grant below names the internal it carries and the issue tracking its removal; do not add a new
// one — publish a capability instead.

// Carries: the internal AddBuiltOptions extension (Configuration/BuiltOptions.cs), consumed at
// Options/ServiceBusOptionsBuilder.cs. Does NOT use ChatterJson. Removal is tracked by issue #478;
// publishing an options-registration capability is the intended replacement.
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.AzureServiceBus")]

// Carries: BrokeredMessageRouter (Routing/BrokeredMessageRouter.cs — implicitly internal, it
// declares no access modifier), MessageContext.MaterializePersistedContext, and five
// ChatterJson.Options call sites. Tracked by issue #412.
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Reliability.Cosmos")]
