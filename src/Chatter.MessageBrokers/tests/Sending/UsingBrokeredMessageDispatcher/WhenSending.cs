using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        private readonly RecordingLoggerCreator<BrokeredMessageDispatcher> _logger;
        private readonly BrokeredMessageDispatcher _sut;

        private readonly Guid _generatedId = Guid.NewGuid();
        private List<OutboundBrokeredMessage> _routedMessages;
        private string _routedInfrastructureType = "never-routed";

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
                          .Callback<IEnumerable<OutboundBrokeredMessage>, TransactionContext, string>(CaptureRouting)
                          .Returns(Task.CompletedTask);

            _logger = New.Common().RecordingLogger<BrokeredMessageDispatcher>();

            _sut = new BrokeredMessageDispatcher(
                _messageRouter.Object,
                _forwarder.Object,
                _detailProvider.Object,
                _bodyConverterFactory.Object,
                _idGenerator.Object,
                _logger.Creation);
        }

        private class FakeCommand : ICommand { }

        private void CaptureRouting(IEnumerable<OutboundBrokeredMessage> outboundMessages, TransactionContext transactionContext, string infrastructureType)
        {
            _routedInfrastructureType = infrastructureType;
            _routedMessages = outboundMessages.ToList();
        }

        private static SendOptions SendOptionsWithInfrastructureType(object infrastructureType)
        {
            var options = new SendOptions();
            options.WithMessageContext(MessageContext.InfrastructureType, infrastructureType);
            return options;
        }

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
            await _sut.Send(new FakeCommand(), "destination");

            // An absent InfrastructureType is not found by the dispatcher's kind-tested read, so the router is handed
            // null: the default Messaging Infrastructure.
            _routedInfrastructureType.Should().BeNull();
        }

        [Fact]
        public async Task MustRouteViaTheDefaultInfrastructureWhenTheInfrastructureTypeIsNotAString()
        {
            await _sut.Send(new FakeCommand(), "destination", transactionContext: null, options: SendOptionsWithInfrastructureType(7L));

            _routedInfrastructureType.Should().BeNull();
            _routedMessages.Should().ContainSingle();
        }

        // The #464 shape: a handler sending through its IMessageHandlerContext inherits the INBOUND Message Context, which
        // a Messaging Infrastructure may have copied verbatim from the delivery's application properties.
        [Fact]
        public async Task MustRouteViaTheDefaultInfrastructureWhenAnInheritedInfrastructureTypeIsNotAString()
        {
            var inboundContext = new Dictionary<string, object> { [MessageContext.InfrastructureType] = 7L };
            var handlerContext = new MessageBrokerContext("inbound-message-id", new byte[] { 9 }, inboundContext, "receiver-path", CancellationToken.None, _bodyConverter.Object);

            await _sut.Send(new FakeCommand(), "destination", handlerContext, new SendOptions());

            _routedInfrastructureType.Should().BeNull();
            _routedMessages.Should().ContainSingle();
        }

        [Fact]
        public async Task MustLogThatTheInfrastructureTypeWasUnreadable()
        {
            await _sut.Send(new FakeCommand(), "destination", transactionContext: null, options: SendOptionsWithInfrastructureType(7L));

            _logger.CountOf(LogLevel.Warning).Should().Be(1);
            _logger.LoggedMessages.Should().ContainSingle(m => m.message.Contains(MessageContext.InfrastructureType));
        }

        [Fact]
        public async Task MustNotLogWhenNoInfrastructureTypeIsPresent()
        {
            await _sut.Send(new FakeCommand(), "destination");
            await _sut.Send(new FakeCommand(), "destination", transactionContext: null, options: SendOptionsWithInfrastructureType(null));

            _logger.CountOf(LogLevel.Warning).Should().Be(0);
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
        public async Task MustRefuseTheSendAsContentTypeRequiredWhenTheContentTypeIsNotAString()
        {
            var options = new SendOptions();
            options.WithMessageContext(MessageContext.ContentType, 7L);

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
                _idGenerator.Object,
                _logger.Creation);

            await sut.Send(new FakeCommand(), "destination");

            _bodyConverterFactory.Verify(f => f.CreateBodyConverter(It.IsAny<string>()), Times.Never);
            _idGenerator.Verify(g => g.GenerateId(It.IsAny<byte[]>()), Times.Never);
        }
    }
}
