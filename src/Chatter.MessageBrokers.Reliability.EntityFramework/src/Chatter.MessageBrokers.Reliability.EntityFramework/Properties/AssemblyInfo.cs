using System.Runtime.CompilerServices;

// TEST grant. Exposing internals to this package's own test assembly is what InternalsVisibleTo is for; it mirrors
// the Chatter.MessageBrokers precedent (Properties/AssemblyInfo.cs) and is a sanctioned, permanent part of this
// assembly. It carries the internal ReliabilityRetentionPurgeService<TContext> and its PurgeOnceAsync seam, which
// the retention integration suite drives directly against a real database.
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Reliability.EntityFramework.Tests")]
