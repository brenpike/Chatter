using Chatter.CQRS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;

namespace Chatter.MessageBrokers.Receiving
{
    class BrokeredMessageReceiverFactory : IBrokeredMessageReceiverFactory
    {
        private readonly IMessagingInfrastructureReceiver _infrastructureReceiver;
        private readonly ILoggerFactory _logger;
        private readonly IServiceScopeFactory _serviceFactory;

        public BrokeredMessageReceiverFactory(IMessagingInfrastructureReceiver infrastructureReceiver,
                                              ILoggerFactory logger,
                                              IServiceScopeFactory serviceFactory)
        {
            _infrastructureReceiver = infrastructureReceiver ?? throw new ArgumentNullException(nameof(infrastructureReceiver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        }

        public IBrokeredMessageReceiver<TMessage> Create<TMessage>(string receivingEntityPath, string errorQueuePath = null, string description = null) where TMessage : class, IMessage 
            => new BrokeredMessageReceiver<TMessage>(
                receivingEntityPath,
                errorQueuePath,
                description ?? receivingEntityPath,
                _infrastructureReceiver,
                _logger.CreateLogger<BrokeredMessageReceiver<TMessage>>(),
                _serviceFactory);
    }
}
