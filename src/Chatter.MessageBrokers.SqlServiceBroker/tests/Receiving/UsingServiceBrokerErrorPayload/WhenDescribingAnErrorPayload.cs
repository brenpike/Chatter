using System;
using System.Text;
using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Receiving.UsingServiceBrokerErrorPayload
{
    public class WhenDescribingAnErrorPayload : Testing.Core.Context
    {
        [Fact]
        public void MustDecodeTheUnicodeErrorDocument()
        {
            var body = Encoding.Unicode.GetBytes(
                "<Error xmlns='http://schemas.microsoft.com/SQL/ServiceBroker/Error'><Code>-8470</Code><Description>Remote service has been dropped.</Description></Error>");

            var result = ServiceBrokerErrorPayload.Describe(body);

            result.Should().Contain("-8470");
            result.Should().Contain("Remote service has been dropped.");
        }

        [Fact]
        public void MustReturnTheSentinelForANullPayload()
        {
            var result = ServiceBrokerErrorPayload.Describe(null);

            result.Should().Be(ServiceBrokerErrorPayload.NoErrorPayloadSentinel);
        }

        [Fact]
        public void MustReturnTheSentinelForAnEmptyPayload()
        {
            var result = ServiceBrokerErrorPayload.Describe(Array.Empty<byte>());

            result.Should().Be(ServiceBrokerErrorPayload.NoErrorPayloadSentinel);
        }
    }
}
