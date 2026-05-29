using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.UsingBrokeredMessageRouter
{
    public class WhenRouting : Testing.Core.Context
    {
        private readonly Mock<IMessagingInfrastructureProvider> _infrastructureProvider = new Mock<IMessagingInfrastructureProvider>();
        private readonly Mock<IMessagingInfrastructureDispatcher> _infrastructureDispatcher = new Mock<IMessagingInfrastructureDispatcher>();
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly BrokeredMessageRouter _sut;

        public WhenRouting()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _infrastructureProvider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(_infrastructureDispatcher.Object);
            _sut = new BrokeredMessageRouter(_infrastructureProvider.Object);
        }

        private OutboundBrokeredMessage CreateOutbound(string destination = "destination", string infrastructureType = null)
        {
            var context = new Dictionary<string, object>();
            if (infrastructureType != null)
            {
                context[MessageContext.InfrastructureType] = infrastructureType;
            }
            return new OutboundBrokeredMessage("message-id", new byte[] { 1 }, context, destination, _bodyConverter.Object);
        }

        [Fact]
        public void MustThrowWhenInfrastructureProviderIsNull()
            => FluentActions.Invoking(() => new BrokeredMessageRouter(null)).Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustResolveDispatcherByOutboundInfrastructureType()
        {
            var outbound = CreateOutbound(infrastructureType: "asb");

            await _sut.Route(outbound, transactionContext: null);

            _infrastructureProvider.Verify(p => p.GetDispatcher("asb"), Times.Once);
        }

        [Fact]
        public async Task MustDispatchSingleMessageThroughResolvedDispatcher()
        {
            var outbound = CreateOutbound();
            var transactionContext = new TransactionContext("receiver");

            await _sut.Route(outbound, transactionContext);

            _infrastructureDispatcher.Verify(d => d.Dispatch(outbound, transactionContext), Times.Once);
        }

        [Fact]
        public async Task MustThrowWhenSingleMessageHasNoDestination()
        {
            // INVARIANT: The public OutboundBrokeredMessage constructor rejects whitespace destinations, so the
            // router's own destination guard is reachable only through the internal JSON constructor, which the
            // test assembly can call. This pins that the single-message Route still guards an empty destination.
            var outbound = new OutboundBrokeredMessage("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "  ");

            await FluentActions.Invoking(async () => await _sut.Route(outbound, transactionContext: null))
                .Should().ThrowAsync<ArgumentNullException>();

            _infrastructureProvider.Verify(p => p.GetDispatcher(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task MustDispatchBatchThroughDispatcherResolvedByInfrastructureTypeArgument()
        {
            var outbounds = new[] { CreateOutbound(), CreateOutbound() };
            var transactionContext = new TransactionContext("receiver");

            await _sut.Route(outbounds, transactionContext, "asb");

            _infrastructureProvider.Verify(p => p.GetDispatcher("asb"), Times.Once);
            _infrastructureDispatcher.Verify(d => d.Dispatch((IEnumerable<OutboundBrokeredMessage>)outbounds, transactionContext), Times.Once);
        }

        [Fact]
        public async Task MustResolveDispatcherWithEmptyStringWhenNoInfrastructureTypeProvidedForBatch()
        {
            var outbounds = new[] { CreateOutbound() };

            await _sut.Route(outbounds, transactionContext: null);

            _infrastructureProvider.Verify(p => p.GetDispatcher(""), Times.Once);
        }
    }
}
