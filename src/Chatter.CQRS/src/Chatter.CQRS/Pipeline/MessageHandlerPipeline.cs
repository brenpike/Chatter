using Chatter.CQRS.Context;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.CQRS.Pipeline
{
    public class MessageHandlerPipeline<TMessage> : IMessageHandlerPipeline<TMessage> where TMessage : IMessage
    {
        private readonly IMessageHandler<TMessage> _handler;
        private readonly IEnumerable<IMessageHandlerPipelineStep> _pipelineSteps;

        public MessageHandlerPipeline(IMessageHandler<TMessage> handler, IEnumerable<IMessageHandlerPipelineStep> pipelineSteps)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _pipelineSteps = pipelineSteps ?? throw new ArgumentNullException(nameof(pipelineSteps));
        }

        public Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext)
        {
            Task theHandler() => _handler.Handle(message, messageHandlerContext);

            return _pipelineSteps
                .Reverse()
                .Aggregate((StepHandler)theHandler, (next, pipeline) => () => pipeline.Handle(message, messageHandlerContext, next))();
        }
    }
}
