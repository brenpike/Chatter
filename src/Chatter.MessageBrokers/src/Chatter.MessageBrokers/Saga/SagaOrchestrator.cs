using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Saga
{
    internal class SagaOrchestrator : ISagaOrchestrator
    {
        private readonly ISagaPersister _sagaPersister;
        private readonly ISagaInitializer _sagaInitializer;
        private readonly ISagaOptionsProvider _sagaOptionsProvider;
        private readonly IBrokeredMessageDispatcher _brokeredMessageDispatcher;

        public SagaOrchestrator(ISagaPersister sagaPersister,
                                ISagaInitializer sagaInitializer,
                                ISagaOptionsProvider sagaOptionsProvider,
                                IBrokeredMessageDispatcher brokeredMessageDispatcher)
        {
            _sagaPersister = sagaPersister ?? throw new ArgumentNullException(nameof(sagaPersister));
            _sagaInitializer = sagaInitializer ?? throw new ArgumentNullException(nameof(sagaInitializer));
            _sagaOptionsProvider = sagaOptionsProvider ?? throw new ArgumentNullException(nameof(sagaOptionsProvider));
            _brokeredMessageDispatcher = brokeredMessageDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageDispatcher));
        }

        public async Task Complete<TMessage>(Func<TMessage, IMessageHandlerContext, Task> sagaStepHandler, ISagaMessage message, IMessageBrokerContext context) where TMessage : IMessage
        {
            SagaContext saga = await _sagaInitializer.Initialize(message, context).ConfigureAwait(false);

            if (!(sagaStepHandler is null))
            {
                await sagaStepHandler((TMessage)message, context).ConfigureAwait(false);
            }

            if (context.Container.TryGet<FailureContext>(out var errorContext))
            {
                saga.Fail(errorContext.ToString());
            }
            else if (context.Container.TryGet<CompensationRoutingContext>(out var compensateContext))
            {
                saga.Fail(compensateContext.ToString());
            }
            else
            {
                saga.Success();
            }

            context.BrokeredMessage.WithSagaStatus(saga.Status.Status);

            await _sagaPersister.Persist(saga, message, context).ConfigureAwait(false);
        }

        public async Task InvokeStep<TMessage>(Func<TMessage, IMessageHandlerContext, Task> sagaStepHandler, ISagaMessage message, IMessageBrokerContext context)
            where TMessage : IMessage
        {
            SagaContext saga = await _sagaInitializer.Initialize(message, context).ConfigureAwait(false);

            if (!(sagaStepHandler is null))
            {
                await sagaStepHandler((TMessage)message, context).ConfigureAwait(false);
            }

            saga.InProgress();

            context.BrokeredMessage.WithSagaStatus(saga.Status.Status);

            await _sagaPersister.Persist(saga, message, context).ConfigureAwait(false);
        }

        public async Task Start<TSagaMessage>(TSagaMessage message, IMessageHandlerContext context)
            where TSagaMessage : ISagaMessage
        {
            SagaContext saga = await _sagaInitializer.Initialize(message, context).ConfigureAwait(false);

            var options = _sagaOptionsProvider.GetOptionsFor(message);

            var sendOptions = new SendOptions()
                .WithSubject(options.Description)
                .WithTimeToLiveInMinutes(options.MaxSagaDurationInMinutes)
                .WithTransactionMode(options.TransactionMode)
                .WithSagaId(saga.SagaId)
                .WithSagaStatus(saga.Status.Status);

            await _brokeredMessageDispatcher.Send(message, options: sendOptions).ConfigureAwait(false);

            saga.InProgress();

            await _sagaPersister.Persist(saga, message, context).ConfigureAwait(false);
        }
    }
}
