using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Saga
{
    public class SagaMessageDispatcher : IDispatchMessages
    {
        private readonly IServiceProvider _serviceFactory;

        public SagaMessageDispatcher(IServiceProvider serviceFactory)
        {
            _serviceFactory = serviceFactory;
        }

        Type IDispatchMessages.DispatchType => typeof(ISagaMessage);

        public Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            var orchestrator = _serviceFactory.GetRequiredService<ISagaOrchestrator>();

            if (message is IStartSagaMessage startSagaMessage)
            {
                return orchestrator.Start(startSagaMessage, messageHandlerContext);
            }

            var context = messageHandlerContext.AsMessageBrokerContext();
            var sagaStepHandler = _serviceFactory.GetService<IMessageHandler<TMessage>>();

            if (message is ICompleteSagaMessage completeSagaMessage)
            {
                if (sagaStepHandler is null)
                {
                    return orchestrator.Complete<TMessage>(null, completeSagaMessage, context);
                }
                else
                {
                    return orchestrator.Complete<TMessage>(sagaStepHandler.Handle, completeSagaMessage, context);
                }
            }

            if (!(message is ISagaMessage sagaMessage))
            {
                throw new ArgumentException(nameof(message), $"'{typeof(SagaMessageDispatcher).Name}' requires a {nameof(TMessage)} of type '{typeof(ISagaMessage)}'.");
            }

            return orchestrator.InvokeStep<TMessage>(sagaStepHandler.Handle, sagaMessage, context);
        }
    }
}
