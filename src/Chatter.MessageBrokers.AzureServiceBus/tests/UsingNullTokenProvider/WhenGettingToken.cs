using Chatter.MessageBrokers.AzureServiceBus;
using FluentAssertions;
using Microsoft.Azure.ServiceBus.Primitives;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.UsingNullTokenProvider
{
    // GetTokenAsync is an explicit ITokenProvider member, so the SUT is referenced through
    // the interface. The returned SecurityToken's ExpiresAtUtc is DateTime.Now (non-deterministic)
    // and is intentionally NOT pinned.
    public class WhenGettingToken : Testing.Core.Context
    {
        private readonly ITokenProvider _sut = new NullTokenProvider();

        [Fact]
        public async Task MustReturnTokenWithHardcodedTokenValue()
        {
            var token = await _sut.GetTokenAsync("appliesTo", TimeSpan.FromMinutes(1));
            token.TokenValue.Should().Be("token");
        }

        [Fact]
        public async Task MustReturnTokenWithHardcodedAudience()
        {
            var token = await _sut.GetTokenAsync("appliesTo", TimeSpan.FromMinutes(1));
            token.Audience.Should().Be("audience");
        }
    }
}
