using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace CarRental.Application.Behaviors
{
    public class LoggingBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : IMessage
    {
        private readonly ILogger<LoggingBehavior<TMessage>> _logger;

        public LoggingBehavior(ILogger<LoggingBehavior<TMessage>> logger)
        {
            _logger = logger;
        }

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            _logger.LogInformation($"Executed '{this.GetType().Name}'. Pre-delegate.");
            await next();
            _logger.LogInformation($"Executed '{this.GetType().Name}'. Post-delegate.");
        }
    }
}
