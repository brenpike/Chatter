using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosContainerFactory
{
    public class WhenDerivingContainers : Testing.Core.Context
    {
        private sealed class CreateOrder : ICommand { }
        private sealed class PostLedgerEntry : ICommand { }

        private static DocumentReliabilityRegistration Registration<TCommand>(string database, string container, string lease,
                                                                              Func<IServiceProvider, Container> documentFactory = null,
                                                                              Func<IServiceProvider, Container> leaseFactory = null)
            where TCommand : ICommand
            => new DocumentReliabilityRegistration(
                typeof(TCommand),
                database,
                container,
                lease,
                _ => new PartitionKey("pk"),
                Array.AsReadOnly(new[] { "/tenantId" }),
                documentFactory,
                leaseFactory);

        private static IServiceProvider ProviderWithClient(CosmosClient client)
        {
            var services = new ServiceCollection();
            if (client is not null)
            {
                services.AddSingleton(client);
            }
            return services.BuildServiceProvider();
        }

        [Fact]
        public void MustDeriveContainerViaGetContainerFromRegisteredClient()
        {
            var container = Mock.Of<Container>();
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer("shop", "orders")).Returns(container);

            var factory = new CosmosContainerFactory(ProviderWithClient(client.Object));
            var resolved = factory.GetDocumentContainer(Registration<CreateOrder>("shop", "orders", "orders-leases"));

            resolved.Should().BeSameAs(container);
            client.Verify(c => c.GetContainer("shop", "orders"), Times.Once);
        }

        [Fact]
        public void MustCacheContainerPerDatabaseAndContainerName()
        {
            var container = Mock.Of<Container>();
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer("shop", "orders")).Returns(container);

            var factory = new CosmosContainerFactory(ProviderWithClient(client.Object));
            var registration = Registration<CreateOrder>("shop", "orders", "orders-leases");

            var first = factory.GetDocumentContainer(registration);
            var second = factory.GetDocumentContainer(registration);

            second.Should().BeSameAs(first);
            // GetContainer is invoked exactly once per (database, container) — the second call is a cache hit.
            client.Verify(c => c.GetContainer("shop", "orders"), Times.Once);
        }

        [Fact]
        public void MustDeriveDistinctContainersForDistinctRegistrations()
        {
            var orders = Mock.Of<Container>();
            var ledger = Mock.Of<Container>();
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer("shop", "orders")).Returns(orders);
            client.Setup(c => c.GetContainer("fin", "ledger")).Returns(ledger);

            var factory = new CosmosContainerFactory(ProviderWithClient(client.Object));

            var resolvedOrders = factory.GetDocumentContainer(Registration<CreateOrder>("shop", "orders", "orders-leases"));
            var resolvedLedger = factory.GetDocumentContainer(Registration<PostLedgerEntry>("fin", "ledger", "ledger-leases"));

            resolvedOrders.Should().BeSameAs(orders);
            resolvedLedger.Should().BeSameAs(ledger);
            client.Verify(c => c.GetContainer("shop", "orders"), Times.Once);
            client.Verify(c => c.GetContainer("fin", "ledger"), Times.Once);
        }

        [Fact]
        public void MustDeriveLeaseContainerViaGetContainer()
        {
            var lease = Mock.Of<Container>();
            var client = new Mock<CosmosClient>();
            client.Setup(c => c.GetContainer("shop", "orders-leases")).Returns(lease);

            var factory = new CosmosContainerFactory(ProviderWithClient(client.Object));
            var resolved = factory.GetLeaseContainer(Registration<CreateOrder>("shop", "orders", "orders-leases"));

            resolved.Should().BeSameAs(lease);
            client.Verify(c => c.GetContainer("shop", "orders-leases"), Times.Once);
        }

        [Fact]
        public void MustUseExplicitFactoryFromAdvancedOverloadInsteadOfGetContainer()
        {
            var explicitContainer = Mock.Of<Container>();
            var client = new Mock<CosmosClient>(MockBehavior.Strict);

            var factory = new CosmosContainerFactory(ProviderWithClient(client.Object));
            var registration = Registration<CreateOrder>(
                "synthetic", "synthetic:document", "synthetic:lease",
                documentFactory: _ => explicitContainer);

            var resolved = factory.GetDocumentContainer(registration);

            resolved.Should().BeSameAs(explicitContainer);
            // The explicit factory bypasses GetContainer entirely — the strict client mock is never called.
            client.Verify(c => c.GetContainer(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public void MustFailLoudlyWhenNoCosmosClientIsRegistered()
        {
            var factory = new CosmosContainerFactory(ProviderWithClient(client: null));

            Action act = () => factory.GetDocumentContainer(Registration<CreateOrder>("shop", "orders", "orders-leases"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*CosmosClient*");
        }
    }
}
