using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.SqlServiceBroker
{
    /// <summary>
    /// Internal <see cref="Func{TResult}"/>-backed fold of the former SqlServiceBrokerReceiverFactory and
    /// SqlServiceBrokerSenderFactory. Implements both public core factory interfaces; each Create
    /// delegate reproduces the prior "open a DI scope, resolve the scoped service, dispose the scope"
    /// behavior exactly (the scoped instance intentionally outlives the transient resolution scope, as
    /// the two original factories did via <c>using var sp = ...</c>).
    /// </summary>
    internal class SqlServiceBrokerInfrastructureFactory : IMessagingInfrastructureReceiverFactory, IMessagingInfrastructureDispatcherFactory
    {
        private readonly Func<IMessagingInfrastructureReceiver> _createReceiver;
        private readonly Func<IMessagingInfrastructureDispatcher> _createDispatcher;

        public SqlServiceBrokerInfrastructureFactory(Func<IMessagingInfrastructureReceiver> createReceiver,
                                                     Func<IMessagingInfrastructureDispatcher> createDispatcher)
        {
            _createReceiver = createReceiver ?? throw new ArgumentNullException(nameof(createReceiver));
            _createDispatcher = createDispatcher ?? throw new ArgumentNullException(nameof(createDispatcher));
        }

        IMessagingInfrastructureReceiver IMessagingInfrastructureReceiverFactory.Create() => _createReceiver();

        IMessagingInfrastructureDispatcher IMessagingInfrastructureDispatcherFactory.Create() => _createDispatcher();
    }
}
