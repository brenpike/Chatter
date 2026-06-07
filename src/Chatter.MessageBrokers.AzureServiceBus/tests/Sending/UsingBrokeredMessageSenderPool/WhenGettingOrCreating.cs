using Chatter.MessageBrokers.AzureServiceBus.Sending;
using FluentAssertions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Sending.UsingBrokeredMessageSenderPool
{
    // Unit tests over BrokeredMessageSenderPool's checkout/return logic driven through the internal
    // IServiceBusMessageSenderFactory seam. The Microsoft.Azure.ServiceBus 5.2.0 MessageSender ctor is
    // lazy (no connection opens until the first send), so real senders can back the pool here as long
    // as no SendAsync is invoked; the three production construction branches themselves remain
    // integration-only (covered by AzureSdkMessageSenderFactory in a live namespace).
    public class WhenGettingOrCreating : Testing.Core.Context
    {
        private const string ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=c2VjcmV0";

        // Records each requested (destination, tuple) and hands back a real (un-sent, owns-connection)
        // MessageSender so Return/reuse can be exercised without opening a connection.
        private class RecordingSenderFactory : IServiceBusMessageSenderFactory
        {
            public List<(string destination, ServiceBusConnection connection, string sendViaPath)> Requests { get; } = new List<(string, ServiceBusConnection, string)>();
            public int CreateCount { get; private set; }

            public MessageSender Create(string destinationEntityPath, (ServiceBusConnection connection, string sendViaPath) receiverConnectionAndPath)
            {
                CreateCount++;
                Requests.Add((destinationEntityPath, receiverConnectionAndPath.connection, receiverConnectionAndPath.sendViaPath));
                return new MessageSender(ConnectionString, destinationEntityPath);
            }
        }

        [Fact]
        public void MustThrowWhenSenderFactoryNull()
        {
            Action act = () => new BrokeredMessageSenderPool((IServiceBusMessageSenderFactory)null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustCreateSenderOnColdCheckout()
        {
            var factory = new RecordingSenderFactory();
            var pool = new BrokeredMessageSenderPool(factory);

            var sender = pool.GetOrCreate("destination", (null, null));

            sender.Should().NotBeNull();
            factory.CreateCount.Should().Be(1);
        }

        [Fact]
        public void MustPassDestinationAndTupleToFactory()
        {
            var factory = new RecordingSenderFactory();
            var pool = new BrokeredMessageSenderPool(factory);

            pool.GetOrCreate("destination", (null, null));

            factory.Requests.Should().ContainSingle()
                .Which.Should().Be(("destination", (ServiceBusConnection)null, (string)null));
        }

        [Fact]
        public void MustReuseReturnedSenderWithoutCreatingAnother()
        {
            var factory = new RecordingSenderFactory();
            var pool = new BrokeredMessageSenderPool(factory);
            var sender = pool.GetOrCreate("destination", (null, null));

            // An owns-connection sender is re-keyed by its Path on Return.
            pool.Return(sender);
            var reused = pool.GetOrCreate("destination", (null, null));

            reused.Should().BeSameAs(sender);
            factory.CreateCount.Should().Be(1);
        }

        [Fact]
        public void MustCreateAnotherWhenNoneReturned()
        {
            var factory = new RecordingSenderFactory();
            var pool = new BrokeredMessageSenderPool(factory);

            pool.GetOrCreate("destination", (null, null));
            pool.GetOrCreate("destination", (null, null));

            factory.CreateCount.Should().Be(2);
        }
    }
}
