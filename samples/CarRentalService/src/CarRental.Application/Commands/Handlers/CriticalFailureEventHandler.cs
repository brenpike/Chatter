using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Recovery;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace CarRental.Application.Commands.Handlers
{
    public class CriticalFailureEventHandler : IMessageHandler<CriticalFailureEvent>
    {
        private readonly ILogger<CriticalFailureEventHandler> _logger;

        public CriticalFailureEventHandler(ILogger<CriticalFailureEventHandler> logger) 
            => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        public Task Handle(CriticalFailureEvent message, IMessageHandlerContext context)
        {
            _logger.LogCritical($"{nameof(CriticalFailureEvent)} received. {message.Context}");
            return Task.CompletedTask;
        }
    }
}
