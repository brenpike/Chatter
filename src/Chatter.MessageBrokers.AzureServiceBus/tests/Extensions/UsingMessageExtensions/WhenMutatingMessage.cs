using Chatter.MessageBrokers.AzureServiceBus.Extensions;
using FluentAssertions;
using Microsoft.Azure.ServiceBus;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Extensions.UsingMessageExtensions
{
    // Microsoft.Azure.ServiceBus.Message constructed directly; UserProperties auto-initializes
    // to an empty dictionary, so the merge/add extensions operate against it without setup.
    public class WhenMutatingMessage : Testing.Core.Context
    {
        private readonly byte[] _body = new byte[] { 1, 2, 3 };

        private Message CreateMessage() => new Message(_body);

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
        public void MustMergeUserProperties()
        {
            var message = CreateMessage();
            message.WithUserProperties(new Dictionary<string, object> { ["a"] = 1, ["b"] = "two" });
            message.UserProperties["a"].Should().Be(1);
            message.UserProperties["b"].Should().Be("two");
        }

        [Fact]
        public void MustOverwriteExistingUserPropertyOnMerge()
        {
            var message = CreateMessage();
            message.UserProperties["a"] = "original";
            message.WithUserProperties(new Dictionary<string, object> { ["a"] = "replaced" });
            message.UserProperties["a"].Should().Be("replaced");
        }

        [Fact]
        public void MustAddSingleUserProperty()
        {
            var message = CreateMessage();
            message.AddUserProperty("key", "value");
            message.UserProperties["key"].Should().Be("value");
        }

        [Fact]
        public void MustReturnSameMessageInstanceFromAddUserProperty()
        {
            var message = CreateMessage();
            message.AddUserProperty("key", "value").Should().BeSameAs(message);
        }
    }
}
