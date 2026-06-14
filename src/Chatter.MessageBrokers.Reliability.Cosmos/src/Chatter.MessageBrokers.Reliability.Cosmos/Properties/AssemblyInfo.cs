using System.Runtime.CompilerServices;

// Mirrors the Chatter.CQRS / Chatter.MessageBrokers test-characterization precedent (see those projects' Properties/AssemblyInfo.cs).
[assembly: InternalsVisibleTo("Chatter.MessageBrokers.Reliability.Cosmos.Tests")]

// Lets Moq (Castle DynamicProxy) create proxies for generic constructions over internal types. The public key is
// Castle's fixed DynamicProxyGenAssembly2 key required for strong-named proxy targets.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7")]
