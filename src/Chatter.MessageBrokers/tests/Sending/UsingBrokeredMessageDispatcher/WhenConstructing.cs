using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingBrokeredMessageDispatcher
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly Mock<IRouteBrokeredMessages> _messageRouter = new Mock<IRouteBrokeredMessages>();
        private readonly Mock<IForwardMessages> _forwarder = new Mock<IForwardMessages>();
        private readonly Mock<IBrokeredMessageAttributeDetailProvider> _detailProvider = new Mock<IBrokeredMessageAttributeDetailProvider>();
        private readonly Mock<IBodyConverterFactory> _bodyConverterFactory = new Mock<IBodyConverterFactory>();
        private readonly Mock<IMessageIdGenerator> _idGenerator = new Mock<IMessageIdGenerator>();

        [Fact]
        public void MustThrowWhenMessageRouterIsNull()
            => FluentActions.Invoking(() => new BrokeredMessageDispatcher(null, _forwarder.Object, _detailProvider.Object, _bodyConverterFactory.Object, _idGenerator.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenForwarderIsNull()
            => FluentActions.Invoking(() => new BrokeredMessageDispatcher(_messageRouter.Object, null, _detailProvider.Object, _bodyConverterFactory.Object, _idGenerator.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenDetailProviderIsNull()
            => FluentActions.Invoking(() => new BrokeredMessageDispatcher(_messageRouter.Object, _forwarder.Object, null, _bodyConverterFactory.Object, _idGenerator.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenBodyConverterFactoryIsNull()
            => FluentActions.Invoking(() => new BrokeredMessageDispatcher(_messageRouter.Object, _forwarder.Object, _detailProvider.Object, null, _idGenerator.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowWhenMessageIdGeneratorIsNull()
            => FluentActions.Invoking(() => new BrokeredMessageDispatcher(_messageRouter.Object, _forwarder.Object, _detailProvider.Object, _bodyConverterFactory.Object, null))
                .Should().Throw<ArgumentNullException>();
    }
}
