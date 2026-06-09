using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Slips;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingMessageHandlerContextExtensions
{
    public class WhenAddingRoutingSlip : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();

        public WhenAddingRoutingSlip()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private MessageBrokerContext CreateContext()
            => new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", CancellationToken.None, _bodyConverter.Object);

        [Fact]
        public void MustIncludeSlipInContainer()
        {
            var context = CreateContext();
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid()).Build();

            context.AddRoutingSlip(slip);

            context.Container.TryGet<RoutingSlip>(out var includedSlip).Should().BeTrue();
            includedSlip.Should().BeSameAs(slip);
        }
    }
}
