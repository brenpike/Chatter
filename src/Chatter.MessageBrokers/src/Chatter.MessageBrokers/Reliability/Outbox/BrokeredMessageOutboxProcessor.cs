using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    internal sealed class BrokeredMessageOutboxProcessor : IHostedService
    {
        private readonly IBrokeredMessageInfrastructureDispatcher _brokeredMessageDispatcher;
        private readonly IMessageDestinationRouter<DestinationRouterContext> _nextDestinationRouter;

        public BrokeredMessageOutboxProcessor(IBrokeredMessageInfrastructureDispatcher brokeredMessageDispatcher, IMessageDestinationRouter<DestinationRouterContext> nextDestinationRouter)
        {
            _brokeredMessageDispatcher = brokeredMessageDispatcher;
            _nextDestinationRouter = nextDestinationRouter;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
