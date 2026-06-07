using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Moq;

namespace Chatter.MessageBrokers.Tests.Receiving.Fakes
{
    /// <summary>
    /// In-memory test double for <see cref="IMessagingInfrastructureProvider"/>.
    /// Returns the supplied <see cref="InMemoryMessagingInfrastructureReceiver"/> from
    /// <see cref="GetReceiver"/> and provides a real <see cref="DefaultBrokeredMessagePathBuilder"/>
    /// via <see cref="GetInfrastructure"/> so
    /// <c>BrokeredMessageReceiver.StartReceiverImpl</c> can resolve the message receiving path.
    /// </summary>
    public sealed class InMemoryMessagingInfrastructureProvider : IMessagingInfrastructureProvider
    {
        private readonly InMemoryMessagingInfrastructureReceiver _receiver;
        private readonly IMessagingInfrastructure _infrastructure;

        public InMemoryMessagingInfrastructureProvider(InMemoryMessagingInfrastructureReceiver receiver)
        {
            _receiver = receiver ?? throw new System.ArgumentNullException(nameof(receiver));

            // INVARIANT: MessagingInfrastructure's two-arg constructor injects DefaultBrokeredMessagePathBuilder
            // internally; the test project has InternalsVisibleTo access so DefaultBrokeredMessagePathBuilder
            // is resolvable, but we use the public MessagingInfrastructure ctor here to stay on the public API.
            var dispatcherFactory = new Mock<IMessagingInfrastructureDispatcherFactory>();
            dispatcherFactory.Setup(f => f.Create()).Returns(new Mock<IMessagingInfrastructureDispatcher>().Object);

            var receiverFactory = new Mock<IMessagingInfrastructureReceiverFactory>();
            receiverFactory.Setup(f => f.Create()).Returns(_receiver);

            _infrastructure = new MessagingInfrastructure(
                type: InfrastructureType,
                receiveInfrastructure: receiverFactory.Object,
                dispatchInfrastructure: dispatcherFactory.Object);
        }

        /// <summary>The infrastructure type key used for all lookups.</summary>
        public const string InfrastructureType = "in-memory-test";

        // ------------------------------------------------------------------ IMessagingInfrastructureProvider

        /// <summary>Returns an <see cref="IMessagingInfrastructure"/> whose PathBuilder is the real
        /// <see cref="DefaultBrokeredMessagePathBuilder"/> so path resolution succeeds.</summary>
        public IMessagingInfrastructure GetInfrastructure(string type)
            => _infrastructure;

        /// <summary>Returns the <see cref="InMemoryMessagingInfrastructureReceiver"/> supplied at construction.</summary>
        public IMessagingInfrastructureReceiver GetReceiver(string type)
            => _receiver;

        /// <summary>Not exercised by the receiver loop; throws to make unexpected calls visible.</summary>
        public IMessagingInfrastructureDispatcher GetDispatcher(string type)
            => throw new System.NotImplementedException(
                $"{nameof(InMemoryMessagingInfrastructureProvider)}.{nameof(GetDispatcher)} is not used by the receiver loop.");
    }
}
