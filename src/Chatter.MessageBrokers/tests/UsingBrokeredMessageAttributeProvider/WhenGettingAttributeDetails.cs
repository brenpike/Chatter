using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingBrokeredMessageAttributeProvider
{
    public class WhenGettingAttributeDetails : Testing.Core.Context
    {
        private readonly BrokeredMessageAttributeProvider _sut = new BrokeredMessageAttributeProvider();

        [BrokeredMessage(
            sendingPath: "sending",
            receivingPath: "receiving",
            errorQueueName: "errorQueue",
            messageDescription: "description",
            infrastructureType: "infra")]
        private class DecoratedMessage { }

        [BrokeredMessage(sendingPath: "sending", receivingPath: "receiving")]
        private class DecoratedMessageWithoutDescription { }

        private class UndecoratedMessage { }

        [Fact]
        public void MustGetMessageNameFromSendingPathViaGeneric()
            => _sut.GetMessageName<DecoratedMessage>().Should().Be("sending");

        [Fact]
        public void MustGetMessageNameFromSendingPathViaType()
            => _sut.GetMessageName(typeof(DecoratedMessage)).Should().Be("sending");

        [Fact]
        public void MustGetReceiverNameFromReceivingPath()
            => _sut.GetReceiverName<DecoratedMessage>().Should().Be("receiving");

        [Fact]
        public void MustGetErrorQueueName()
            => _sut.GetErrorQueueName<DecoratedMessage>().Should().Be("errorQueue");

        [Fact]
        public void MustGetInfrastructureType()
            => _sut.GetInfrastructureType<DecoratedMessage>().Should().Be("infra");

        [Fact]
        public void MustGetMessageDescriptionWhenDescriptionIsSet()
            => _sut.GetBrokeredMessageDescription<DecoratedMessage>().Should().Be("description");

        [Fact]
        public void MustFallBackToReceiverNameForDescriptionWhenDescriptionIsNotSet()
            => _sut.GetBrokeredMessageDescription<DecoratedMessageWithoutDescription>().Should().Be("receiving");

        [Fact]
        public void MustReturnNullMessageNameWhenTypeIsNotDecorated()
            => _sut.GetMessageName<UndecoratedMessage>().Should().BeNull();

        [Fact]
        public void MustReturnNullMessageNameViaTypeWhenTypeIsNotDecorated()
            => _sut.GetMessageName(typeof(UndecoratedMessage)).Should().BeNull();

        [Fact]
        public void MustReturnNullReceiverNameWhenTypeIsNotDecorated()
            => _sut.GetReceiverName<UndecoratedMessage>().Should().BeNull();

        [Fact]
        public void MustReturnNullErrorQueueNameWhenTypeIsNotDecorated()
            => _sut.GetErrorQueueName<UndecoratedMessage>().Should().BeNull();

        [Fact]
        public void MustReturnNullInfrastructureTypeWhenTypeIsNotDecorated()
            => _sut.GetInfrastructureType<UndecoratedMessage>().Should().BeNull();

        // INVARIANT: GetBrokeredMessageDescription uses the non-null-conditional
        // TryGetBrokeredMessageAttribute() and dereferences MessageDescription, so it
        // throws when the type is not decorated (unlike the other getters which use ?.).
        [Fact]
        public void MustThrowWhenGettingDescriptionForUndecoratedType()
            => FluentActions.Invoking(() => _sut.GetBrokeredMessageDescription<UndecoratedMessage>())
                .Should().Throw<NullReferenceException>();
    }
}
