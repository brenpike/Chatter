using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Saga
{
    public interface ISagaOrchestrator
    {
        Task Start<TSagaMessage>(TSagaMessage message, IMessageHandlerContext context)
            where TSagaMessage : ISagaMessage;

        Task InvokeStep<TMessage>(Func<TMessage, IMessageHandlerContext, Task> sagaStepHandler, ISagaMessage message, IMessageBrokerContext context)
            where TMessage : IMessage;

        Task Complete<TMessage>(Func<TMessage, IMessageHandlerContext, Task> sagaStepHandler, ISagaMessage message, IMessageBrokerContext context)
            where TMessage : IMessage;
    }
}
