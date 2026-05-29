using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingBrokeredMessageDispatcher
{
    public class WhenSending : Testing.Core.Context
    {
        private readonly Mock<IRouteBrokeredMessages> _messageRouter = new Mock<IRouteBrokeredMessages>();
        private readonly Mock<IForwardMessages> _forwarder = new Mock<IForwardMessages>();
        private readonly Mock<IBrokeredMessageAttributeDetailProvider> _detailProvider = new Mock<IBrokeredMessageAttributeDetailProvider>();
        private readonly Mock<IBodyConverterFactory> _bodyConverterFactory = new Mock<IBodyConverterFactory>();
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IMessageIdGenerator> _idGenerator = new Mock<IMessageIdGenerator>();
        private readonly BrokeredMessageDispatcher _sut;

        private readonly Guid _generatedId = Guid.NewGuid();
        private List<OutboundBrokeredMessage> _routedMessages;

        public WhenSending()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _bodyConverter.Setup(c => c.Convert(It.IsAny<object>())).Returns(new byte[] { 1, 2, 3 });
            _bodyConverterFactory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(_bodyConverter.Object);
            _idGenerator.Setup(g => g.GenerateId(It.IsAny<byte[]>())).Returns(_generatedId);

            // INVARIANT: BrokeredMessageDispatcher.Dispatch projects messages via a deferred (yield return)
            // iterator. The body conversion, id generation and destination resolution only run when the router
            // enumerates the sequence. The default router setup therefore forces enumeration so the build-out
            // behavior is observable; the deferred quirk itself is pinned in MustNotBuildOutboundMessageWhenRouterDoesNotEnumerate.
            _messageRouter.Setup(r => r.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<string>()))
                          .Callback<IEnumerable<OutboundBrokeredMessage>, TransactionContext, string>((m, _, __) => _routedMessages = m.ToList())
                          .Returns(Task.CompletedTask);

            _sut = new BrokeredMessageDispatcher(
                _messageRouter.Object,
                _forwarder.Object,
                _detailProvider.Object,
                _bodyConverterFactory.Object,
                _idGenerator.Object);
        }

        private class FakeCommand : ICommand { }

        [Fact]
        public async Task MustResolveBodyConverterViaFactoryUsingContentType()
        {
            await _sut.Send(new FakeCommand(), "destination");
            // INVARIANT: SendOptions defaults ContentType to RoutingOptions.DefaultContentType ("application/json").
            _bodyConverterFactory.Verify(f => f.CreateBodyConverter("application/json"), Times.Once);
        }

        [Fact]
        public async Task MustConvertMessageBodyViaBodyConverter()
        {
            var message = new FakeCommand();
            await _sut.Send(message, "destination");
            _bodyConverter.Verify(c => c.Convert(message), Times.Once);
        }

        [Fact]
        public async Task MustStampMessageIdViaIdGeneratorWhenNoMessageIdProvided()
        {
            await _sut.Send(new FakeCommand(), "destination");
            _idGenerator.Verify(g => g.GenerateId(It.IsAny<byte[]>()), Times.Once);
        }

        [Fact]
        public async Task MustRouteToProvidedDestination()
        {
            await _sut.Send(new FakeCommand(), "queue/path");
            _routedMessages.Single().Destination.Should().Be("queue/path");
        }

        [Fact]
        public async Task MustResolveDestinationViaDetailProviderWhenNoDestinationPathProvided()
        {
            _detailProvider.Setup(p => p.GetMessageName(It.IsAny<Type>())).Returns("resolved-destination");

            await _sut.Send(new FakeCommand());

            _detailProvider.Verify(p => p.GetMessageName(typeof(FakeCommand)), Times.Once);
            _routedMessages.Single().Destination.Should().Be("resolved-destination");
        }

        [Fact]
        public async Task MustRouteViaMessageRouterOnce()
        {
            await _sut.Send(new FakeCommand(), "destination");
            _messageRouter.Verify(r => r.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task MustPassTransactionContextThroughToRouter()
        {
            var transactionContext = new TransactionContext("receiver", TransactionMode.ReceiveOnly);
            await _sut.Send(new FakeCommand(), "destination", transactionContext);
            _messageRouter.Verify(r => r.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), transactionContext, It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task MustPassNullInfrastructureTypeWhenNoneInMessageContext()
        {
            string infraType = "sentinel";
            _messageRouter.Setup(r => r.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<string>()))
                          .Callback<IEnumerable<OutboundBrokeredMessage>, TransactionContext, string>((_, __, i) => infraType = i)
                          .Returns(Task.CompletedTask);

            await _sut.Send(new FakeCommand(), "destination");

            // INVARIANT: Dispatch casts the missing InfrastructureType out-value (null) to string, yielding null.
            infraType.Should().BeNull();
        }

        [Fact]
        public async Task MustUseProvidedMessageIdWhenSendOptionsSupplyOne()
        {
            var options = new SendOptions { MessageId = "explicit-id" };

            await _sut.Send(new FakeCommand(), "destination", transactionContext: null, options: options);

            _routedMessages.Single().MessageId.Should().Be("explicit-id");
            _idGenerator.Verify(g => g.GenerateId(It.IsAny<byte[]>()), Times.Never);
        }

        [Fact]
        public async Task MustThrowArgumentNullWhenContentTypeIsWhitespace()
        {
            var options = new SendOptions { ContentType = "  " };
            await FluentActions.Invoking(async () => await _sut.Send(new FakeCommand(), "destination", transactionContext: null, options: options))
                .Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task MustThrowArgumentNullWhenDestinationCannotBeResolved()
        {
            _detailProvider.Setup(p => p.GetMessageName(It.IsAny<Type>())).Returns((string)null);
            await FluentActions.Invoking(async () => await _sut.Send(new FakeCommand()))
                .Should().ThrowAsync<ArgumentNullException>();
        }

        // INVARIANT (characterization of deferred-iterator quirk): the per-message body conversion and id
        // generation live inside a deferred (yield return) projection, so when the router does not enumerate the
        // outbound sequence they never run. This pins current behavior, not desired behavior.
        [Fact]
        public async Task MustNotBuildOutboundMessageWhenRouterDoesNotEnumerate()
        {
            var nonEnumeratingRouter = new Mock<IRouteBrokeredMessages>();
            nonEnumeratingRouter.Setup(r => r.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<string>()))
                                .Returns(Task.CompletedTask);
            var sut = new BrokeredMessageDispatcher(
                nonEnumeratingRouter.Object,
                _forwarder.Object,
                _detailProvider.Object,
                _bodyConverterFactory.Object,
                _idGenerator.Object);

            await sut.Send(new FakeCommand(), "destination");

            _bodyConverterFactory.Verify(f => f.CreateBodyConverter(It.IsAny<string>()), Times.Never);
            _idGenerator.Verify(g => g.GenerateId(It.IsAny<byte[]>()), Times.Never);
        }
    }
}
