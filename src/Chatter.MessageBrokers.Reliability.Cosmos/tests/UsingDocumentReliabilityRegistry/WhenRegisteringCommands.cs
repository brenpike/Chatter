using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingDocumentReliabilityRegistry
{
    public class WhenRegisteringCommands : Testing.Core.Context
    {
        private sealed class CreateOrder : ICommand { }
        private sealed class PostLedgerEntry : ICommand { }

        private static DocumentReliabilityRegistration Registration<TCommand>(string database = "shop", string container = "orders")
            where TCommand : ICommand
            => new DocumentReliabilityRegistration(
                typeof(TCommand),
                database,
                container,
                container + "-leases",
                _ => new PartitionKey("pk"),
                Array.AsReadOnly(new[] { "/tenantId" }));

        // Add is internal; the test project sees it via InternalsVisibleTo on the production assembly.
        private static void Add(DocumentReliabilityRegistry registry, DocumentReliabilityRegistration registration)
            => registry.Add(registration);

        [Fact]
        public void MustRegisterMultipleCommandsToDistinctRegistrations()
        {
            var registry = new DocumentReliabilityRegistry();
            Add(registry, Registration<CreateOrder>(database: "shop", container: "orders"));
            Add(registry, Registration<PostLedgerEntry>(database: "fin", container: "ledger"));

            registry.TryGet(typeof(CreateOrder), out var orderReg).Should().BeTrue();
            registry.TryGet(typeof(PostLedgerEntry), out var ledgerReg).Should().BeTrue();

            orderReg.ContainerName.Should().Be("orders");
            ledgerReg.ContainerName.Should().Be("ledger");
            orderReg.Should().NotBeSameAs(ledgerReg);
        }

        [Fact]
        public void MustReturnTrueAndRegistrationOnHit()
        {
            var registry = new DocumentReliabilityRegistry();
            var registration = Registration<CreateOrder>();
            Add(registry, registration);

            registry.TryGet(typeof(CreateOrder), out var resolved).Should().BeTrue();
            resolved.Should().BeSameAs(registration);
        }

        [Fact]
        public void MustReturnFalseOnMiss()
        {
            var registry = new DocumentReliabilityRegistry();
            Add(registry, Registration<CreateOrder>());

            registry.TryGet(typeof(PostLedgerEntry), out var resolved).Should().BeFalse();
            resolved.Should().BeNull();
        }

        [Fact]
        public void MustThrowOnDuplicateRegistrationForSameCommandType()
        {
            var registry = new DocumentReliabilityRegistry();
            Add(registry, Registration<CreateOrder>(container: "orders"));

            Action duplicate = () => Add(registry, Registration<CreateOrder>(container: "orders-v2"));

            duplicate.Should().Throw<InvalidOperationException>()
                .WithMessage("*CreateOrder*");
        }
    }
}
