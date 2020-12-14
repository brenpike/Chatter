using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    class CriticalFailureEventDispatcher : ICriticalFailureNotifier
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CriticalFailureEventDispatcher> _logger;

        public CriticalFailureEventDispatcher(IServiceScopeFactory scopeFactory, ILogger<CriticalFailureEventDispatcher> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Notify(FailureContext failureContext)
        {
            _logger.LogTrace($"Dispatching '{nameof(CriticalFailureEvent)}'. MessageId: '{failureContext.Inbound.MessageId}', CorrelationId: '{failureContext.Inbound.CorrelationId}'");
            using var scope = _scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetService<IMessageDispatcher>();

            if (dispatcher != null)
            {
                await dispatcher.Dispatch(new CriticalFailureEvent() { Context = failureContext }).ConfigureAwait(false);
                _logger.LogTrace($"Dispatched '{nameof(CriticalFailureEvent)}'.");
            }
            else
            {
                _logger.LogTrace($"{nameof(CriticalFailureEvent)} not dispatched. No {nameof(IMessageDispatcher)} registered.");
            }
        }
    }
}
