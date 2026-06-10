using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using Moq;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Tests.Routing.Slips
{
    internal static class RoutingSlipDispatcherMock
    {
        // INVARIANT: Send<TMessage> is a strongly-typed generic overload, so Moq matches the setup per
        // concrete TMessage. The factory MUST be generic over each fixture's FakeMessage type, otherwise
        // the setup would not match the strongly-typed call and the routing-slip await would observe a
        // null Task (NullReferenceException) before assertions run.
        public static Mock<IBrokeredMessageDispatcher> Completed<TMessage>() where TMessage : ICommand
        {
            var dispatcher = new Mock<IBrokeredMessageDispatcher>();

            dispatcher
                .Setup(d => d.Send(It.IsAny<TMessage>(), It.IsAny<string>(), It.IsAny<TransactionContext>(), It.IsAny<SendOptions>()))
                .Returns(Task.CompletedTask);

            dispatcher
                .Setup(d => d.Forward(It.IsAny<InboundBrokeredMessage>(), It.IsAny<string>(), It.IsAny<TransactionContext>()))
                .Returns(Task.CompletedTask);

            return dispatcher;
        }
    }
}
