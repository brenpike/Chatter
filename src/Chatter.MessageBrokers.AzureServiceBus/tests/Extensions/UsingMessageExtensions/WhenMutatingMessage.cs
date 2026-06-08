using Chatter.MessageBrokers.AzureServiceBus.Extensions;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Extensions.UsingMessageExtensions
{
    // Azure.Messaging.ServiceBus.ServiceBusMessage constructed directly; ApplicationProperties
    // auto-initializes to an empty dictionary, so the merge/add extensions operate against it without setup.
    public class WhenMutatingMessage : Testing.Core.Context
    {
        private readonly byte[] _body = new byte[] { 1, 2, 3 };

        private ServiceBusMessage CreateMessage() => new ServiceBusMessage(BinaryData.FromBytes(_body));

        [Fact]
        public void MustComputeUppercaseSha256HexMessageIdWhenIdIsBlank()
        {
            // INVARIANT: hash is the SHA256 of body {1,2,3} formatted with ToString("X2") (uppercase hex).
            var message = CreateMessage().WithHashedBodyMessageId("");
            message.MessageId.Should().Be("039058C6F2C0CB492C533B0A4D14EF77CC0F78ABCCCED5287D84A1A2011CFB81");
        }

        [Fact]
        public void MustComputeHashedMessageIdWhenIdIsWhitespace()
        {
            var message = CreateMessage().WithHashedBodyMessageId("   ");
            message.MessageId.Should().Be("039058C6F2C0CB492C533B0A4D14EF77CC0F78ABCCCED5287D84A1A2011CFB81");
        }

        [Fact]
        public void MustUseSuppliedMessageIdWhenNotBlank()
        {
            var message = CreateMessage().WithHashedBodyMessageId("supplied-id");
            message.MessageId.Should().Be("supplied-id");
        }

        [Fact]
        public void MustReturnSameMessageInstanceFromWithHashedBodyMessageId()
        {
            var message = CreateMessage();
            message.WithHashedBodyMessageId("id").Should().BeSameAs(message);
        }

        [Fact]
        public void MustMergeApplicationProperties()
        {
            var message = CreateMessage();
            message.WithApplicationProperties(new Dictionary<string, object> { ["a"] = 1, ["b"] = "two" });
            message.ApplicationProperties["a"].Should().Be(1);
            message.ApplicationProperties["b"].Should().Be("two");
        }

        [Fact]
        public void MustOverwriteExistingApplicationPropertyOnMerge()
        {
            var message = CreateMessage();
            message.ApplicationProperties["a"] = "original";
            message.WithApplicationProperties(new Dictionary<string, object> { ["a"] = "replaced" });
            message.ApplicationProperties["a"].Should().Be("replaced");
        }

        [Fact]
        public void MustAddSingleApplicationProperty()
        {
            var message = CreateMessage();
            message.AddApplicationProperty("key", "value");
            message.ApplicationProperties["key"].Should().Be("value");
        }

        [Fact]
        public void MustReturnSameMessageInstanceFromAddApplicationProperty()
        {
            var message = CreateMessage();
            message.AddApplicationProperty("key", "value").Should().BeSameAs(message);
        }
    }
}
