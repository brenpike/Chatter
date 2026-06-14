using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.DependencyInjection.UsingExtensions
{
    // Characterization tests pinning the OBSERVABLE wiring contract of AddSqlServiceBroker: the
    // ServiceDescriptor shape that the production registration leaves on the IServiceCollection — the
    // service types, lifetimes, and the presence of an IMessagingInfrastructure factory descriptor.
    //
    // These assert the wiring contract (which service types are registered, at which lifetimes, and that
    // IMessagingInfrastructure is produced through a Singleton factory) — NOT the name of the concrete
    // infrastructure factory type. They therefore survive the STEP-006 rewire from the per-broker
    // SqlServiceBrokerInfrastructureFactory to the shared core factory unchanged.
    //
    // SCOPE (descriptor-shape, not full resolution): these tests deliberately pin the wiring at the
    // IServiceCollection descriptor level — against the real, unmodified AddSqlServiceBroker output, which
    // performs no AppDomain scan on its own — rather than resolving IMessagingInfrastructure end-to-end
    // through AddChatterCqrs / AddMessageBrokers.
    //
    // HISTORICAL NOTE: this descriptor-shape approach originally existed because full bootstrapping was not
    // reachable in the SSB test AppDomain. Both AddChatterCqrs and AddMessageBrokers call
    // AssemblySourceFilter.Apply(), which enumerates AppDomain.CurrentDomain.GetAssemblies() and calls
    // GetTypes() on every loaded assembly; the legacy System.Data.SqlClient 4.6.x assembly (transitively
    // referenced by this module at the time) threw ReflectionTypeLoadException ("Could not load type
    // 'SqlGuidCaster' ... incorrectly aligned or overlapped") on that scan. The migration to
    // Microsoft.Data.SqlClient (#204) removed System.Data.SqlClient from this module's dependency graph, so
    // that specific crash no longer blocks the scan. A full-resolution DI test that exercises the real
    // Chatter bootstrap path is now worth adding (tracked separately); these descriptor-shape characterization
    // tests are retained as the always-available wiring-contract pin regardless of AppDomain scan behavior.
    public class WhenAddingSqlServiceBroker : Testing.Core.Context
    {
        private const string _connectionString =
            "Server=test;Database=test;Trusted_Connection=True;";

        private static IConfiguration EmptyConfig()
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

        // Runs the real AddSqlServiceBroker against a bare ChatterBuilder (no AddChatterCqrs / AddMessageBrokers,
        // so no AssemblySourceFilter.Apply() AppDomain scan) and returns the resulting service collection. The
        // AddSqlServiceBrokerOptions(connectionString) overload is used because WithConnectionString alone
        // assumes a previously-constructed options object — the string overload constructs it.
        private static IServiceCollection BuildRegistration()
        {
            var services = new ServiceCollection();
            var filter = AssemblySourceFilterBuilder.New().Build();
            var builder = ChatterBuilder.Create(services, EmptyConfig(), filter);

            builder.AddSqlServiceBroker(o => o.AddSqlServiceBrokerOptions(_connectionString));

            return services;
        }

        private static ServiceDescriptor Single(IServiceCollection services, Type serviceType)
            => services.Single(d => d.ServiceType == serviceType);

        [Fact]
        public void MustRegisterMessagingInfrastructureAsSingletonViaFactory()
        {
            // Pins the IMessagingInfrastructure descriptor: a single Singleton registration produced by a
            // factory delegate (the lambda that builds MessagingInfrastructure from the resolved
            // infrastructure factory). Asserting the factory PRESENCE — not the concrete factory type —
            // survives the STEP-006 rewire.
            var services = BuildRegistration();

            var descriptor = Single(services, typeof(IMessagingInfrastructure));

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationFactory.Should().NotBeNull();
            descriptor.ImplementationInstance.Should().BeNull();
            descriptor.ImplementationType.Should().BeNull();
        }

        [Fact]
        public void MustRegisterSqlConnectionSourceAsScoped()
        {
            // The connection source is Scoped because it is injected into the Scoped receiver and sender.
            // ISqlConnectionSource is internal; this test assembly has InternalsVisibleTo, so the closed
            // service type is referenceable. Pinned via reflection-free descriptor lookup over the type.
            var services = BuildRegistration();

            var connectionSourceType =
                typeof(SSBMessageContext).Assembly.GetType(
                    "Chatter.MessageBrokers.SqlServiceBroker.Receiving.ISqlConnectionSource", throwOnError: true);

            var descriptor = Single(services, connectionSourceType);

            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterReceiverAndSenderAsScoped()
        {
            // The infrastructure factory delegates open a fresh DI scope per Create and resolve these Scoped
            // services, so the receiver and sender must be registered Scoped for the fresh-scope-per-Create
            // behavior to hold. Both types are internal; resolved by name through the IVT'd assembly.
            var services = BuildRegistration();

            var brokerAssembly = typeof(SSBMessageContext).Assembly;
            var receiverType = brokerAssembly.GetType(
                "Chatter.MessageBrokers.SqlServiceBroker.Receiving.SqlServiceBrokerReceiver", throwOnError: true);
            var senderType = brokerAssembly.GetType(
                "Chatter.MessageBrokers.SqlServiceBroker.Sending.SqlServiceBrokerSender", throwOnError: true);

            Single(services, receiverType).Lifetime.Should().Be(ServiceLifetime.Scoped);
            Single(services, senderType).Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterSqlServiceBrokerOptionsAsSingletonInstance()
        {
            // The built SqlServiceBrokerOptions are registered as a singleton instance carrying the supplied
            // connection string — the value the receiver/sender materialize into a live connection at runtime.
            var services = BuildRegistration();

            var descriptor = Single(services, typeof(SqlServiceBrokerOptions));

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationInstance.Should().BeOfType<SqlServiceBrokerOptions>()
                      .Which.ConnectionString.Should().Be(_connectionString);
        }

        [Fact]
        public void MustNotRegisterCustomPathBuilder()
        {
            // Pins the documented behavior-preservation divergence from ASB: SqlServiceBroker registers NO
            // broker-specific IBrokeredMessagePathBuilder. The MessagingInfrastructure 3-arg ctor therefore
            // falls back to the core default DefaultBrokeredMessagePathBuilder. (ASB instead registers its own
            // AzureServiceBusEntityPathBuilder and uses the 4-arg ctor.) This assertion survives the STEP-006
            // rewire because the 3-arg ctor path is preserved.
            var services = BuildRegistration();

            services.Should().NotContain(d => d.ServiceType == typeof(IBrokeredMessagePathBuilder));
        }
    }
}
