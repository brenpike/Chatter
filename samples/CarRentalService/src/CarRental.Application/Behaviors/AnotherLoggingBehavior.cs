using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace CarRental.Application.Behaviors
{
    public class AnotherLoggingBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : IMessage
    {
        private readonly ILogger<AnotherLoggingBehavior<TMessage>> _logger;

        public AnotherLoggingBehavior(ILogger<AnotherLoggingBehavior<TMessage>> logger)
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
