using Chatter.CQRS.DependencyInjection;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.DependencyInjection.UsingExtensions
{
    // Pins the OBSERVABLE wiring contract of AddRabbitMq at the IServiceCollection descriptor level (the SSB
    // approach): which service types are registered, at which lifetimes, that IMessagingInfrastructure is a
    // Singleton factory descriptor, and that FullAtomicityViaInfrastructure is rejected at registration.
    // AddRabbitMq runs against a BARE ChatterBuilder (no AddChatterCqrs/AddMessageBrokers, so no
    // AssemblySourceFilter.Apply() AppDomain scan), mirroring WhenAddingSqlServiceBroker.
    public class WhenAddingRabbitMq : Testing.Core.Context
    {
        private static IConfiguration EmptyConfig()
            => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

        private static IServiceCollection BuildRegistration(Action<IServiceCollection> preconfigure = null)
        {
            var services = new ServiceCollection();
            preconfigure?.Invoke(services);
            var filter = AssemblySourceFilterBuilder.New().Build();
            var builder = ChatterBuilder.Create(services, EmptyConfig(), filter);

            builder.AddRabbitMq(o => o.AddRabbitMqOptions(hostName: "localhost"));

            return services;
        }

        private static ServiceDescriptor Single(IServiceCollection services, Type serviceType)
            => services.Single(d => d.ServiceType == serviceType);

        private static Type ConnectionSourceType()
            => typeof(RabbitMqMessageContext).Assembly.GetType(
                "Chatter.MessageBrokers.RabbitMQ.Receiving.IRabbitMqConnectionSource", throwOnError: true);

        [Fact]
        public void MustRegisterMessagingInfrastructureAsSingletonViaFactory()
        {
            var services = BuildRegistration();

            var descriptor = Single(services, typeof(IMessagingInfrastructure));

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationFactory.Should().NotBeNull();
            descriptor.ImplementationInstance.Should().BeNull();
            descriptor.ImplementationType.Should().BeNull();
        }

        // INVARIANT: the one deliberate lifetime divergence from the SSB fold — the AMQP connection source is a
        // process SINGLETON (SSB's connection source is Scoped), because one IConnection is owned per process.
        [Fact]
        public void MustRegisterConnectionSourceAsSingleton()
        {
            var services = BuildRegistration();

            Single(services, ConnectionSourceType()).Lifetime.Should().Be(ServiceLifetime.Singleton);
        }

        [Fact]
        public void MustRegisterReceiverAndSenderAsScoped()
        {
            var services = BuildRegistration();

            var brokerAssembly = typeof(RabbitMqMessageContext).Assembly;
            var receiverType = brokerAssembly.GetType(
                "Chatter.MessageBrokers.RabbitMQ.Receiving.RabbitMqReceiver", throwOnError: true);
            var senderType = brokerAssembly.GetType(
                "Chatter.MessageBrokers.RabbitMQ.Sending.RabbitMqSender", throwOnError: true);

            Single(services, receiverType).Lifetime.Should().Be(ServiceLifetime.Scoped);
            Single(services, senderType).Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterPredicateProvidersAsSingleton()
        {
            var services = BuildRegistration();

            Single(services, typeof(ICircuitBreakerExceptionPredicatesProvider))
                .Lifetime.Should().Be(ServiceLifetime.Singleton);
            Single(services, typeof(IRetryExceptionPredicatesProvider))
                .Lifetime.Should().Be(ServiceLifetime.Singleton);
        }

        [Fact]
        public void MustRegisterPathBuilderAsSingleton()
        {
            var services = BuildRegistration();

            var pathBuilderType = typeof(RabbitMqMessageContext).Assembly.GetType(
                "Chatter.MessageBrokers.RabbitMQ.RabbitMqPathBuilder", throwOnError: true);

            Single(services, pathBuilderType).Lifetime.Should().Be(ServiceLifetime.Singleton);
        }

        [Fact]
        public void MustRegisterBodyConverterAsScoped()
        {
            var services = BuildRegistration();

            Single(services, typeof(IBrokeredMessageBodyConverter))
                .Lifetime.Should().Be(ServiceLifetime.Scoped);
        }

        [Fact]
        public void MustRegisterRabbitMqOptionsAsSingletonInstance()
        {
            var services = BuildRegistration();

            var descriptor = Single(services, typeof(Chatter.MessageBrokers.RabbitMQ.Configuration.RabbitMqOptions));

            descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
            descriptor.ImplementationInstance.Should()
                .BeOfType<Chatter.MessageBrokers.RabbitMQ.Configuration.RabbitMqOptions>()
                .Which.HostName.Should().Be("localhost");
        }

        // --- FullAtomicityViaInfrastructure rejection at registration -----------------------------------

        // MessageBrokerOptions.TransactionMode has an internal setter (set by core configuration); set it via
        // reflection to model a host that configured the global mode to FullAtomicityViaInfrastructure.
        private static MessageBrokerOptions GlobalOptions(TransactionMode mode)
        {
            var options = new MessageBrokerOptions();
            typeof(MessageBrokerOptions).GetProperty(nameof(MessageBrokerOptions.TransactionMode))
                .SetValue(options, mode);
            return options;
        }

        [Fact]
        public void MustThrowWhenGlobalTransactionModeIsFullAtomicity()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton(GlobalOptions(TransactionMode.FullAtomicityViaInfrastructure)));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void MustThrowWhenAnAttributedRabbitMqReceiverRequestsFullAtomicity()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(new ReceiverOptions
                    {
                        InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                        TransactionMode = TransactionMode.FullAtomicityViaInfrastructure
                    })));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void MustNotThrowWhenReceiverRequestsReceiveOnly()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(new ReceiverOptions
                    {
                        InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                        TransactionMode = TransactionMode.ReceiveOnly
                    })));

            act.Should().NotThrow();
        }

        [Fact]
        public void MustNotThrowWhenGlobalTransactionModeIsNone()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton(GlobalOptions(TransactionMode.None)));

            act.Should().NotThrow();
        }

        // A non-RabbitMQ receiver requesting FullAtomicity must NOT be claimed by AddRabbitMq when RabbitMQ is
        // not the resolved default (it cannot be — an IMessagingInfrastructure descriptor already exists).
        [Fact]
        public void MustNotThrowWhenAtomicReceiverBelongsToAnotherInfrastructure()
        {
            Action act = () => BuildRegistration(services =>
            {
                services.AddSingleton<IMessagingInfrastructure>(new ForeignMessagingInfrastructure());
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(new ReceiverOptions
                    {
                        InfrastructureType = "Chatter.Infrastructure.SomeOtherBroker",
                        TransactionMode = TransactionMode.FullAtomicityViaInfrastructure
                    }));
            });

            act.Should().NotThrow();
        }

        // --- Multiple-RabbitMQ-receiver rejection at registration --------------------------------------

        // Two RabbitMQ-attributed receivers must throw at registration: the singleton connection source owns one
        // receive channel and one consumer registration, so a second receiver would clobber the first.
        [Fact]
        public void MustThrowWhenMoreThanOneRabbitMqReceiverIsDiscovered()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(
                        new ReceiverOptions
                        {
                            InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                            TransactionMode = TransactionMode.ReceiveOnly
                        },
                        new ReceiverOptions
                        {
                            InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                            TransactionMode = TransactionMode.ReceiveOnly
                        })));

            act.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void MustNotThrowWhenExactlyOneRabbitMqReceiverIsDiscovered()
        {
            Action act = () => BuildRegistration(services =>
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(new ReceiverOptions
                    {
                        InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                        TransactionMode = TransactionMode.ReceiveOnly
                    })));

            act.Should().NotThrow();
        }

        // Receivers belonging to ANOTHER infrastructure must NOT count toward the single-RabbitMQ-receiver limit. A
        // ForeignMessagingInfrastructure descriptor exists, so RabbitMQ is not the resolved default and the two
        // foreign receivers are not claimed: only the one RabbitMQ receiver counts, so no throw.
        [Fact]
        public void MustNotThrowWhenAdditionalReceiversBelongToAnotherInfrastructure()
        {
            Action act = () => BuildRegistration(services =>
            {
                services.AddSingleton<IMessagingInfrastructure>(new ForeignMessagingInfrastructure());
                services.AddSingleton<IDiscoveredReceiverRegistry>(
                    new StubDiscoveredReceiverRegistry(
                        new ReceiverOptions
                        {
                            InfrastructureType = RabbitMqMessageContext.InfrastructureType,
                            TransactionMode = TransactionMode.ReceiveOnly
                        },
                        new ReceiverOptions
                        {
                            InfrastructureType = "Chatter.Infrastructure.SomeOtherBroker",
                            TransactionMode = TransactionMode.ReceiveOnly
                        },
                        new ReceiverOptions
                        {
                            InfrastructureType = "Chatter.Infrastructure.SomeOtherBroker",
                            TransactionMode = TransactionMode.ReceiveOnly
                        }));
            });

            act.Should().NotThrow();
        }

        private sealed class StubDiscoveredReceiverRegistry : IDiscoveredReceiverRegistry
        {
            private readonly List<ReceiverOptions> _receivers = new List<ReceiverOptions>();

            public StubDiscoveredReceiverRegistry(params ReceiverOptions[] receivers)
                => _receivers.AddRange(receivers);

            public void Register(ReceiverOptions options) => _receivers.Add(options);
            public IReadOnlyCollection<ReceiverOptions> DiscoveredReceivers => _receivers;
        }

        // A stand-in IMessagingInfrastructure so RabbitMQ is NOT the resolved default in the foreign-receiver
        // test (the default is "first-registered" and is decided by descriptor presence, not resolution).
        private sealed class ForeignMessagingInfrastructure : IMessagingInfrastructure
        {
            public string Type => "Chatter.Infrastructure.SomeOtherBroker";
            public IMessagingInfrastructureReceiver ReceiveInfrastructure => throw new NotImplementedException();
            public Chatter.MessageBrokers.Sending.IMessagingInfrastructureDispatcher DispatchInfrastructure => throw new NotImplementedException();
            public IBrokeredMessagePathBuilder PathBuilder => throw new NotImplementedException();
        }
    }
}
