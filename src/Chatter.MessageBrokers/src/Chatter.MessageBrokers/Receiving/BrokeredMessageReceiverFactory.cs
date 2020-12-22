using Chatter.CQRS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;

namespace Chatter.MessageBrokers.Receiving
{
    class BrokeredMessageReceiverFactory : IBrokeredMessageReceiverFactory
    {
        private readonly IMessagingInfrastructureProvider _infrastructureProvider;
        private readonly ILoggerFactory _logger;
        private readonly IServiceScopeFactory _serviceFactory;

        public BrokeredMessageReceiverFactory(IMessagingInfrastructureProvider infrastructureProvider,
                                              ILoggerFactory logger,
                                              IServiceScopeFactory serviceFactory)
        {
            _infrastructureProvider = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        }

        public IBrokeredMessageReceiver<TMessage> Create<TMessage>(ReceiverOptions options) where TMessage : class, IMessage
            => new BrokeredMessageReceiver<TMessage>(
                options,
                _infrastructureProvider,
                _logger.CreateLogger<BrokeredMessageReceiver<TMessage>>(),
                _serviceFactory);
    }
}
