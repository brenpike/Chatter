using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.Testing.Core.Creators.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Reliability.Inbox.UsingInMemoryBrokeredMessageInbox
{
    public class WhenReceivingViaInbox : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly LoggerCreator<InMemoryBrokeredMessageInbox> _logger;
        private readonly InMemoryBrokeredMessageInbox _sut;

        public WhenReceivingViaInbox()
        {
            _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            _logger = New.Common().Logger<InMemoryBrokeredMessageInbox>();
            _sut = new InMemoryBrokeredMessageInbox(_logger.Creation);
        }

        private Mock<IMessageBrokerContext> CreateContext(string messageId)
        {
            var inbound = new InboundBrokeredMessage(messageId, new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", _bodyConverter.Object);
            var context = new Mock<IMessageBrokerContext>();
            context.SetupGet(c => c.BrokeredMessage).Returns(inbound);
            return context;
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenLoggerIsNull()
            => FluentActions.Invoking(() => new InMemoryBrokeredMessageInbox(null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public async Task MustInvokeMessageReceiverOnFirstReceipt()
        {
            var invoked = false;
            await _sut.ReceiveViaInbox<object>(new object(), CreateContext("id-1").Object, () => { invoked = true; return Task.CompletedTask; });
            invoked.Should().BeTrue();
        }

        [Fact]
        public async Task MustNotInvokeMessageReceiverOnDuplicateReceipt()
        {
            var context = CreateContext("id-1").Object;
            await _sut.ReceiveViaInbox<object>(new object(), context, () => Task.CompletedTask);

            var invokedSecondTime = false;
            await _sut.ReceiveViaInbox<object>(new object(), context, () => { invokedSecondTime = true; return Task.CompletedTask; });
            invokedSecondTime.Should().BeFalse();
        }

        [Fact]
        public async Task MustLogTraceForAlreadyReceivedMessageOnDuplicate()
        {
            var context = CreateContext("id-1").Object;
            await _sut.ReceiveViaInbox<object>(new object(), context, () => Task.CompletedTask);
            await _sut.ReceiveViaInbox<object>(new object(), context, () => Task.CompletedTask);

            _logger.VerifyWasCalled(LogLevel.Trace,
                "Brokered message of type 'Object' with id: 'id-1' was already received.",
                Times.Once());
        }

        [Fact]
        public async Task MustLogTraceWhenSuccessfullyAddedToInbox()
        {
            await _sut.ReceiveViaInbox<object>(new object(), CreateContext("id-1").Object, () => Task.CompletedTask);

            _logger.VerifyWasCalled(LogLevel.Trace,
                "Brokered message of type 'Object' with id: 'id-1' was successfully received and added to inbox.",
                Times.Once());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task MustThrowArgumentExceptionWhenMessageIdIsNullOrWhitespace(string messageId)
            => await FluentActions.Invoking(async () =>
                    await _sut.ReceiveViaInbox<object>(new object(), CreateContext(messageId).Object, () => Task.CompletedTask))
                .Should().ThrowAsync<ArgumentException>();

        [Fact]
        public async Task MustPropagateExceptionFromMessageReceiver()
            => await FluentActions.Invoking(async () =>
                    await _sut.ReceiveViaInbox<object>(new object(), CreateContext("id-1").Object, () => throw new InvalidOperationException("boom")))
                .Should().ThrowAsync<InvalidOperationException>();

        [Fact]
        public async Task MustNotRecordMessageWhenReceiverThrows()
        {
            var context = CreateContext("id-1").Object;
            await FluentActions.Invoking(async () =>
                    await _sut.ReceiveViaInbox<object>(new object(), context, () => throw new InvalidOperationException("boom")))
                .Should().ThrowAsync<InvalidOperationException>();

            // INVARIANT: the inbox records the id only after the receiver completes, so a failed
            // receipt leaves the id absent and a retry re-invokes the receiver.
            var invokedOnRetry = false;
            await _sut.ReceiveViaInbox<object>(new object(), context, () => { invokedOnRetry = true; return Task.CompletedTask; });
            invokedOnRetry.Should().BeTrue();
        }
    }
}
