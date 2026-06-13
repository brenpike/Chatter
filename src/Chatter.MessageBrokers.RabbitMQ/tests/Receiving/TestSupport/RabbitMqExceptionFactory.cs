using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System.Threading;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.TestSupport
{
    // Constructs the RabbitMQ.Client transient exceptions that require non-trivial arguments, so the predicate
    // tests can build real instances rather than mock the sealed exception types. Shared by the retry and
    // circuit-breaker predicate suites.
    internal static class RabbitMqExceptionFactory
    {
        public static BrokerUnreachableException BrokerUnreachable()
            => new BrokerUnreachableException(new System.Exception("no broker reachable"));

        public static AlreadyClosedException AlreadyClosed()
            => new AlreadyClosedException(ShutdownReason());

        private static ShutdownEventArgs ShutdownReason()
            => new ShutdownEventArgs(ShutdownInitiator.Library,
                                     replyCode: 320,
                                     replyText: "connection closed",
                                     cause: null,
                                     cancellationToken: CancellationToken.None);
    }
}
