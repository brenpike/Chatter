using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Slips;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingMessageHandlerContextExtensions
{
    public class WhenTryingToGetRoutingSlip : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly RecordingLoggerCreator<RoutingSlip> _logger;

        public WhenTryingToGetRoutingSlip()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _logger = New.Common().RecordingLogger<RoutingSlip>();
        }

        private MessageBrokerContext CreateContext(IDictionary<string, object> messageContext)
            => new MessageBrokerContext("message-id", new byte[] { 1 }, messageContext, "receiver-path", CancellationToken.None, _bodyConverter.Object);

        [Fact]
        public void MustReturnTrueAndSlipWhenBrokerContextCarriesSerializedSlip()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .Build();
            var context = CreateContext(new Dictionary<string, object>());
            context.BrokeredMessage.WithRoutingSlip(slip);

            IMessageHandlerContext asHandlerContext = context;
            var found = asHandlerContext.TryGetRoutingSlip(out var foundSlip);

            found.Should().BeTrue();
            foundSlip.Should().NotBeNull();
            foundSlip.Route[0].DestinationPath.Should().Be("first");
        }

        [Fact]
        public void MustReturnFalseWhenContextIsNotBrokerContext()
        {
            var nonBrokerContext = new Mock<IMessageHandlerContext>().Object;

            var found = nonBrokerContext.TryGetRoutingSlip(out var foundSlip);

            found.Should().BeFalse();
            foundSlip.Should().BeNull();
        }

        [Fact]
        public void MustReturnFalseWhenNoSlipPresent()
        {
            var context = CreateContext(new Dictionary<string, object>());

            IMessageHandlerContext asHandlerContext = context;
            var found = asHandlerContext.TryGetRoutingSlip(out var foundSlip);

            found.Should().BeFalse();
            foundSlip.Should().BeNull();
        }

        [Fact]
        public void MustReturnFalseInsteadOfThrowingOnMalformedSlipValue()
        {
            // INVARIANT: a slip value that is not a string is MALFORMED, and a malformed slip surfaces as
            // false — it is TYPE-TESTED, never cast. TryGetRoutingSlip no longer swallows exceptions: only
            // the four malformed shapes (non-string, null/whitespace string, unparsable JSON, JSON null)
            // return false; every other exception propagates.
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.RoutingSlip] = 12345
            };
            var context = CreateContext(messageContext);

            IMessageHandlerContext asHandlerContext = context;
            var found = asHandlerContext.TryGetRoutingSlip(out var foundSlip);

            found.Should().BeFalse();
            foundSlip.Should().BeNull();
        }

        [Fact]
        public void MustLogWarningNamingMessageIdAndReturnFalseWhenSlipValueIsNotValidJson()
        {
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.RoutingSlip] = "{not-json"
            };
            var context = CreateContext(messageContext);

            IMessageHandlerContext asHandlerContext = context;
            var found = asHandlerContext.TryGetRoutingSlip(_logger.Creation, out var foundSlip);

            found.Should().BeFalse();
            foundSlip.Should().BeNull();
            _logger.CountOf(LogLevel.Warning).Should().Be(1);
            _logger.LoggedMessages.Should().ContainSingle(m => m.message.Contains("message-id"));
        }

        [Fact]
        public void MustLogWarningAndReturnFalseWhenSlipValueIsWhitespace()
        {
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.RoutingSlip] = "   "
            };
            var context = CreateContext(messageContext);

            IMessageHandlerContext asHandlerContext = context;
            var found = asHandlerContext.TryGetRoutingSlip(_logger.Creation, out var foundSlip);

            found.Should().BeFalse();
            foundSlip.Should().BeNull();
            _logger.CountOf(LogLevel.Warning).Should().Be(1);
        }

        [Fact]
        public void MustReturnFalseWhenSlipValueDeserializesToNull()
        {
            // A JSON null parses successfully but yields no slip. Returning true with a null out value
            // breaks the Try-contract, so it is MALFORMED.
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.RoutingSlip] = "null"
            };
            var context = CreateContext(messageContext);

            IMessageHandlerContext asHandlerContext = context;
            var found = asHandlerContext.TryGetRoutingSlip(_logger.Creation, out var foundSlip);

            found.Should().BeFalse();
            foundSlip.Should().BeNull();
            _logger.CountOf(LogLevel.Warning).Should().Be(1);
        }

        [Fact]
        public void MustPropagateExceptionThatIsNotAMalformedSlip()
        {
            // INVARIANT: only malformed slip values are absorbed. A genuine fault reading the brokered
            // message is an operator signal and must NOT be reported as "no slip present".
            var brokerContext = new Mock<IMessageBrokerContext>();
            brokerContext.SetupGet(c => c.BrokeredMessage).Throws(new InvalidOperationException("boom"));

            IMessageHandlerContext asHandlerContext = brokerContext.Object;

            FluentActions.Invoking(() => asHandlerContext.TryGetRoutingSlip(_logger.Creation, out _))
                .Should().Throw<InvalidOperationException>();
            _logger.CountOf(LogLevel.Warning).Should().Be(0);
        }
    }
}
