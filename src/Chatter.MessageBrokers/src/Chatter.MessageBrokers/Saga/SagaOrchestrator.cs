using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Saga
{
    internal class SagaOrchestrator : ISagaOrchestrator
    {
        private readonly ISagaPersister _sagaPersister;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IMessageDestinationRouter _messageDestinationRouter;
        private readonly ISagaInitializer _sagaInitializer;
        private readonly ISagaOptionsProvider _sagaOptionsProvider;
        private readonly IBrokeredMessageDetailProvider _brokeredMessageDetailProvider;

        public SagaOrchestrator(ISagaPersister sagaPersister,
                                IBodyConverterFactory bodyConverterFactory,
                                IMessageDestinationRouter messageDestinationRouter,
                                ISagaInitializer sagaInitializer,
                                ISagaOptionsProvider sagaOptionsProvider,
                                IBrokeredMessageDetailProvider brokeredMessageDetailProvider)
        {
            _sagaPersister = sagaPersister ?? throw new ArgumentNullException(nameof(sagaPersister));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _messageDestinationRouter = messageDestinationRouter ?? throw new ArgumentNullException(nameof(messageDestinationRouter));
            _sagaInitializer = sagaInitializer ?? throw new ArgumentNullException(nameof(sagaInitializer));
            _sagaOptionsProvider = sagaOptionsProvider ?? throw new ArgumentNullException(nameof(sagaOptionsProvider));
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
        }

        public async Task Complete<TMessage>(Func<TMessage, IMessageHandlerContext, Task> sagaStepHandler, ISagaMessage message, IMessageBrokerContext context) where TMessage : IMessage
        {
            SagaContext saga = await _sagaInitializer.Initialize(message, context).ConfigureAwait(false);

            if (!(sagaStepHandler is null))
            {
                await sagaStepHandler((TMessage)message, context).ConfigureAwait(false);
            }

            if (context.Container.TryGet<ErrorContext>(out var errorContext))
            {
                saga.Fail(errorContext.ToString());
            }
            else if (context.Container.TryGet<CompensateContext>(out var compensateContext))
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

            OutboundBrokeredMessage outbound = CreateSagaInputMessage(message, saga.SagaId, saga.Status.Status);
            await _messageDestinationRouter.Route(outbound, null).ConfigureAwait(false);

            saga.InProgress();

            await _sagaPersister.Persist(saga, message, context).ConfigureAwait(false);
        }

        private OutboundBrokeredMessage CreateSagaInputMessage<TSagaMessage>(TSagaMessage message, string sagaId, SagaStatusEnum sagaStatus)
            where TSagaMessage : ISagaMessage
        {
            var options = _sagaOptionsProvider.GetOptionsFor(message);
            var transactionMode = options.TransactionMode;
            var inputQueue = _brokeredMessageDetailProvider.GetMessageName(message.GetType());

            var bodyConverter = _bodyConverterFactory.CreateBodyConverter(options.SagaDataContentType);
            return new OutboundBrokeredMessage(Guid.NewGuid().ToString(), message, inputQueue, bodyConverter)
                .WithSubject(options.Description)
                .WithTimeToLiveInMinutes(options.MaxSagaDurationInMinutes)
                .WithTransactionMode(transactionMode)
                .WithSagaId(sagaId)
                .WithSagaStatus(sagaStatus);
        }
    }
}
