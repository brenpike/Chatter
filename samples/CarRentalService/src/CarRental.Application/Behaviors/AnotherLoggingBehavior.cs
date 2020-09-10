using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace CarRental.Application.Behaviors
{
    public class AnotherLoggingBehavior : ICommandBehavior
    {
        private readonly ILogger<AnotherLoggingBehavior> _logger;

        public AnotherLoggingBehavior(ILogger<AnotherLoggingBehavior> logger)
        {
            _logger = logger;
        }

        public async Task Handle<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) where TMessage : IMessage
        {
            _logger.LogInformation($"Executed '{this.GetType().Name}'. Pre-delegate.");
            await next();
            _logger.LogInformation($"Executed '{this.GetType().Name}'. Post-delegate.");
        }
    }
}
