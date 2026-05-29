using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Outbox.UsingOutboxBrokeredMessageRouter
{
    public class WhenRouting : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageOutbox> _outbox = new Mock<IBrokeredMessageOutbox>();
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly OutboxBrokeredMessageRouter _sut;

        public WhenRouting()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            // INVARIANT: OutboxBrokeredMessageRouter.Route(single) calls the SINGLE SendToOutbox overload directly;
            // Route(batch) calls the BATCH overload directly. The router does NOT fan a single message into the batch
            // overload itself — the single-to-batch delegation lives in the IBrokeredMessageOutbox default interface
            // method, which does NOT execute through a Moq proxy (an unconfigured single overload returns a null Task
            // and throws on await). Both overloads are therefore set up and verified independently.
            _outbox.Setup(o => o.SendToOutbox(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);
            _outbox.Setup(o => o.SendToOutbox(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);
            _sut = new OutboxBrokeredMessageRouter(_outbox.Object);
        }

        private OutboundBrokeredMessage CreateOutbound()
            => new OutboundBrokeredMessage("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "destination", _bodyConverter.Object);

        [Fact]
        public void MustThrowWhenOutboxIsNull()
            => FluentActions.Invoking(() => new OutboxBrokeredMessageRouter(null)).Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustWriteSingleMessageToOutboxViaSingleOverload()
        {
            var outbound = CreateOutbound();
            var transactionContext = new TransactionContext("receiver");

            await _sut.Route(outbound, transactionContext);

            _outbox.Verify(o => o.SendToOutbox(outbound, transactionContext, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustWriteBatchToOutbox()
        {
            var outbounds = new[] { CreateOutbound(), CreateOutbound() };
            var transactionContext = new TransactionContext("receiver");

            await _sut.Route(outbounds, transactionContext, "asb");

            _outbox.Verify(o => o.SendToOutbox((IEnumerable<OutboundBrokeredMessage>)outbounds, transactionContext, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustIgnoreInfrastructureTypeWhenWritingBatchToOutbox()
        {
            var outbounds = new[] { CreateOutbound() };

            await _sut.Route(outbounds, transactionContext: null, infrastructureType: "ignored-infra");

            // INVARIANT: The outbox SendToOutbox signature has no infrastructureType parameter, so the argument
            // supplied to Route is silently dropped. This pins the current behavior.
            _outbox.Verify(o => o.SendToOutbox(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
