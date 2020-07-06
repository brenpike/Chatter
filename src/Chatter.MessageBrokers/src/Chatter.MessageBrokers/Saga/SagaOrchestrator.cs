using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Saga
{
    internal class SagaOrchestrator : ISagaOrchestrator
    {
        private readonly ISagaPersister _sagaPersister;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IBrokeredMessageDispatcher _brokeredMessageDispatcher;
        private readonly ISagaInitializer _sagaInitializer;
        private readonly ISagaOptionsProvider _sagaOptionsProvider;
        private readonly IBrokeredMessageDetailProvider _brokeredMessageDetailProvider;

        public SagaOrchestrator(ISagaPersister sagaPersister,
                                IBodyConverterFactory bodyConverterFactory,
                                IBrokeredMessageDispatcher brokeredMessageDispatcher,
                                ISagaInitializer sagaInitializer,
                                ISagaOptionsProvider sagaOptionsProvider,
                                IBrokeredMessageDetailProvider brokeredMessageDetailProvider)
        {
            _sagaPersister = sagaPersister ?? throw new ArgumentNullException(nameof(sagaPersister));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _brokeredMessageDispatcher = brokeredMessageDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageDispatcher));
            _sagaInitializer = sagaInitializer ?? throw new ArgumentNullException(nameof(sagaInitializer));
            _sagaOptionsProvider = sagaOptionsProvider ?? throw new ArgumentNullException(nameof(sagaOptionsProvider));
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
        }

        public async Task Complete<TMessage>(Func<TMessage, IMessageHandlerContext, Task> sagaStepHandler, ISagaMessage message, IMessageBrokerContext context) where TMessage : IMessage
        {
            SagaContext saga = await _sagaInitializer.Initialize(message, context).ConfigureAwait(false);

            //TODO: things left to do:
            //      4) implement cancellation
            //      6) updating headers on CompleteAsync (i.e., sagastatus isn't updated correctly on successful completion)
            //      7) I don't like how saga states are updated
            //      20) stretch - sagapersistancecontext - hold persistance connection info to ensure same persistance is used for diff processes
            //      21) stretch - sagarestartstrategy

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
            await _brokeredMessageDispatcher.Dispatch(outbound, null).ConfigureAwait(false);

            saga.InProgress();

            await _sagaPersister.Persist(saga, message, context).ConfigureAwait(false);
        }

        private OutboundBrokeredMessage CreateSagaInputMessage<TSagaMessage>(TSagaMessage message, string sagaId, SagaStatusEnum sagaStatus)
            where TSagaMessage : ISagaMessage
        {
            var options = _sagaOptionsProvider.GetOptionsFor(message);
            var transactionMode = TransactionMode.FullAtomicity;
            var inputQueue = _brokeredMessageDetailProvider.GetMessageName(message.GetType());

            var bodyConverter = _bodyConverterFactory.CreateBodyConverter(options.SagaDataContentType);
            return new OutboundBrokeredMessage(message, inputQueue, bodyConverter)
                .WithSubject(options.Description)
                .WithTimeToLiveInMinutes(options.MaxSagaDurationInMinutes)
                .WithTransactionMode(transactionMode)
                .WithSagaId(sagaId)
                .WithSagaStatus(sagaStatus);
        }
    }
}
