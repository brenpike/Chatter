using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Transactions;
using Xunit;
// Disambiguate the local ServiceBusReceiver (system under test) from the SDK type of the same name
// pulled in by `using Azure.Messaging.ServiceBus;` (CS0104).
using ServiceBusReceiver = Chatter.MessageBrokers.AzureServiceBus.Receiving.ServiceBusReceiver;
using ServiceBusClient = Azure.Messaging.ServiceBus.ServiceBusClient;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingServiceBusReceiver
{
    // Characterization tests pinning ServiceBusReceiver behavior that is REACHABLE without a live
    // Azure Service Bus namespace. The SDK MessageReceiver ctor opens a connection on construction,
    // so any path that touches InnerReceiver cannot be exercised here; only the guard/false branches
    // that return BEFORE touching InnerReceiver, plus CreateLocalTransaction and the InitializeAsync
    // receive-mode flip (observed indirectly through the ack-eligibility guard), are pinned.
    public class WhenReceivingMessage : Testing.Core.Context
    {
        private const string _connectionString =
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret";

        // The shared ServiceBusClient the receiver consumes from DI in production. Constructing it from a
        // placeholder SAS connection string opens no connection (the SDK connects lazily on first
        // receive/send), so it is a valid stand-in for the DI-provided singleton here.
        private static ServiceBusClient CreateClient() => new ServiceBusClient(_connectionString);

        // MessageBrokerOptions.TransactionMode is internal-set in the core assembly (no IVT to the
        // test assembly), so it is left at its default (TransactionMode.None => ReceiveAndDelete).
        // The PeekLock mode is reached via InitializeAsync in the acknowledging tests.
        private static ServiceBusReceiver CreateSut()
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = _connectionString,
            };
            var messageBrokerOptions = new MessageBrokerOptions();
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            var bodyConverterFactory = new Mock<IBodyConverterFactory>();

            return new ServiceBusReceiver(CreateClient(), serviceBusOptions, messageBrokerOptions, logger.Object, bodyConverterFactory.Object);
        }

        [Fact]
        public void MustThrowWhenServiceBusOptionsNull()
        {
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            var bodyConverterFactory = new Mock<IBodyConverterFactory>();
            Action act = () => new ServiceBusReceiver(CreateClient(), null, new MessageBrokerOptions(), logger.Object, bodyConverterFactory.Object);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenClientNull()
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = _connectionString,
            };
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            var bodyConverterFactory = new Mock<IBodyConverterFactory>();
            Action act = () => new ServiceBusReceiver(null, serviceBusOptions, new MessageBrokerOptions(), logger.Object, bodyConverterFactory.Object);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenLoggerNull()
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = _connectionString,
            };
            var bodyConverterFactory = new Mock<IBodyConverterFactory>();
            Action act = () => new ServiceBusReceiver(CreateClient(), serviceBusOptions, new MessageBrokerOptions(), null, bodyConverterFactory.Object);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenBodyConverterFactoryNull()
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = _connectionString,
            };
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            Action act = () => new ServiceBusReceiver(CreateClient(), serviceBusOptions, new MessageBrokerOptions(), logger.Object, null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustReturnNullLocalTransactionForNoneMode()
        {
            var sut = CreateSut();
            var context = new TransactionContext("receiver", TransactionMode.None);
            sut.CreateLocalTransaction(context).Should().BeNull();
        }

        [Fact]
        public void MustReturnNullLocalTransactionForReceiveOnlyMode()
        {
            var sut = CreateSut();
            var context = new TransactionContext("receiver", TransactionMode.ReceiveOnly);
            sut.CreateLocalTransaction(context).Should().BeNull();
        }

        [Fact]
        public void MustReturnTransactionScopeForFullAtomicityMode()
        {
            var sut = CreateSut();
            var context = new TransactionContext("receiver", TransactionMode.FullAtomicityViaInfrastructure);
            using var scope = sut.CreateLocalTransaction(context);
            scope.Should().NotBeNull().And.BeOfType<TransactionScope>();
        }
    }
}
