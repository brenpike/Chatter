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
        private readonly IBrokeredMessagePathBuilder _brokeredMessagePathBuilder;

        public BrokeredMessageReceiverFactory(IMessagingInfrastructureProvider infrastructureProvider,
                                              ILoggerFactory logger,
                                              IServiceScopeFactory serviceFactory,
                                              IBrokeredMessagePathBuilder brokeredMessagePathBuilder)
        {
            _infrastructureProvider = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _brokeredMessagePathBuilder = brokeredMessagePathBuilder ?? throw new ArgumentNullException(nameof(brokeredMessagePathBuilder));
        }

        public IBrokeredMessageReceiver<TMessage> Create<TMessage>(ReceiverOptions options) where TMessage : class, IMessage 
            => new BrokeredMessageReceiver<TMessage>(
                options,
                _infrastructureProvider,
                _logger.CreateLogger<BrokeredMessageReceiver<TMessage>>(),
                _serviceFactory,
                _brokeredMessagePathBuilder);
    }
}
