using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Routing;
using Moq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.UsingErrorQueueDispatcher
{
    public class WhenExecuting : Testing.Core.Context
    {
        private readonly Mock<IForwardMessages> _forwardMessages = new Mock<IForwardMessages>();
        private readonly ErrorQueueDispatcher _sut;

        public WhenExecuting()
        {
            _forwardMessages.Setup(f => f.Route(It.IsAny<InboundBrokeredMessage>(), It.IsAny<string>(), It.IsAny<TransactionContext>()))
                            .Returns(Task.CompletedTask);
            _sut = new ErrorQueueDispatcher(_forwardMessages.Object);
        }

        [Fact]
        public async Task MustForwardInboundToErrorQueueName()
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.SetupGet(c => c.ContentType).Returns("application/json");
            var inbound = new InboundBrokeredMessage("inbound-message-id", new byte[] { 1, 2, 3 },
                new System.Collections.Generic.Dictionary<string, object>(), "receiver-path", converter.Object);
            var transactionContext = new TransactionContext("receiver");
            var failureContext = new FailureContext(inbound, "error-queue", "failure-description",
                new System.InvalidOperationException("boom"), 1, transactionContext);

            await _sut.ExecuteAsync(failureContext);

            _forwardMessages.Verify(f => f.Route(inbound, "error-queue", transactionContext), Times.Once);
        }
    }
}
