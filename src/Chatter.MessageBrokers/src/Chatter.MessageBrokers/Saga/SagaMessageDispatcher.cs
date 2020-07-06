using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Saga
{
    public class SagaMessageDispatcher : IMessageDispatcher
    {
        private readonly IServiceScopeFactory _serviceFactory;

        public SagaMessageDispatcher(IServiceScopeFactory serviceFactory)
        {
            _serviceFactory = serviceFactory;
        }

        public Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage
        {
            return Dispatch(message, new MessageHandlerContext());
        }

        public Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            using var scope = _serviceFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ISagaOrchestrator>();

            if (message is IStartSagaMessage startSagaMessage)
            {
                return orchestrator.Start(startSagaMessage, messageHandlerContext);
            }

            var context = messageHandlerContext.AsMessageBrokerContext();
            var sagaStepHandler = scope.ServiceProvider.GetService<IMessageHandler<TMessage>>();

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
