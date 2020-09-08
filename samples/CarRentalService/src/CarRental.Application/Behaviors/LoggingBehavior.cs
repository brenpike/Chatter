using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace CarRental.Application.Behaviors
{
    public class LoggingBehavior : IMessageHandlerPipelineStep
    {
        private readonly ILogger<LoggingBehavior> _logger;

        public LoggingBehavior(ILogger<LoggingBehavior> logger)
        {
            _logger = logger;
        }

        public async Task Handle<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, StepHandler next) where TMessage : IMessage
        {
            _logger.LogInformation("Start handler");
            await next();
            _logger.LogInformation("Finish handler");
        }
    }
}
