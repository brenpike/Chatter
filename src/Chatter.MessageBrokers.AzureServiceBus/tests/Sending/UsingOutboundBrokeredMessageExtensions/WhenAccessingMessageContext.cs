using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Sending.UsingOutboundBrokeredMessageExtensions
{
    public class WhenAccessingMessageContext : Testing.Core.Context
    {
        private readonly byte[] _body = new byte[] { 1, 2, 3 };
        private readonly JsonBodyConverter _converter = new JsonBodyConverter();

        private OutboundBrokeredMessage CreateSut()
            => new OutboundBrokeredMessage("message-id", _body, new Dictionary<string, object>(), "destination", _converter);

        [Fact]
        public void MustRoundTripTo()
        {
            var sut = CreateSut();
            sut.WithTo("the-to").Should().BeSameAs(sut);
            sut.GetToAddress().Should().Be("the-to");
        }

        [Fact]
        public void MustRoundTripViaPartitionKey()
        {
            var sut = CreateSut();
            sut.WithViaPartitionKey("via").Should().BeSameAs(sut);
            sut.GetViaPartitionKey().Should().Be("via");
        }

        [Fact]
        public void MustRoundTripPartitionKey()
        {
            var sut = CreateSut();
            sut.WithPartitionKey("pk").Should().BeSameAs(sut);
            sut.GetPartitionKey().Should().Be("pk");
        }

        [Fact]
        public void MustRoundTripScheduledEnqueueTimeUtc()
        {
            var when = new DateTime(2026, 5, 29, 12, 0, 0, DateTimeKind.Utc);
            var sut = CreateSut();
            sut.WithScheduledEnqueueTimeUtc(when).Should().BeSameAs(sut);
            sut.GetScheduledEnqueueTimeUtc().Should().Be(when);
        }

        [Fact]
        public void MustReturnNullScheduledEnqueueTimeUtcWhenAbsent()
            => CreateSut().GetScheduledEnqueueTimeUtc().Should().BeNull();

        [Fact]
        public void MustReturnNullToAddressWhenAbsent()
            => CreateSut().GetToAddress().Should().BeNull();

        [Fact]
        public void MustReturnApplicationPropertyWhenPresent()
        {
            var sut = CreateSut().WithTo("the-to");
            sut.GetApplicationPropertyByKey(ASBMessageContext.To).Should().Be("the-to");
        }

        [Fact]
        public void MustReturnNullApplicationPropertyWhenAbsent()
            => CreateSut().GetApplicationPropertyByKey("missing").Should().BeNull();
    }
}
