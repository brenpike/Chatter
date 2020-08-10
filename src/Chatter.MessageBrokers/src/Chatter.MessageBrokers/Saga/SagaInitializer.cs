using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Saga
{
    class SagaInitializer : ISagaInitializer
    {
        private readonly ISagaPersister _sagaPersister;

        public SagaInitializer(ISagaPersister sagaPersister)
        {
            _sagaPersister = sagaPersister ?? throw new ArgumentNullException(nameof(sagaPersister));
        }

        public async Task<SagaContext> Initialize(ISagaMessage message, IMessageHandlerContext context)
        {
            if (context.Container.TryGet<SagaContext>(out var saga))
            {
                return saga;
            }

            var inbound = context.GetInboundBrokeredMessage();

            if (inbound != null && inbound.ApplicationProperties.TryGetValue(ApplicationProperties.SagaId, out var sagaId))
            {
                saga = await _sagaPersister.GetById((string)sagaId).ConfigureAwait(false);
            }

            if (saga is null)
            {
                saga = new SagaContext();
                context.Container.Include(saga);
                await _sagaPersister.Persist(saga, message, context).ConfigureAwait(false);
            }

            return saga;
        }
    }
}
