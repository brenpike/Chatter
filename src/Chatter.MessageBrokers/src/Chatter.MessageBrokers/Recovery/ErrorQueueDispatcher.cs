using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    public class ErrorQueueDispatcher : IMaxReceivesExceededAction
    {
        private readonly IForwardMessages _forwardMessages;

        public ErrorQueueDispatcher(IForwardMessages forwardMessages)
            => _forwardMessages = forwardMessages;

        public async Task ExecuteAsync(FailureContext failureContext)
        {
            await _forwardMessages.Route(failureContext.Inbound, failureContext.ErrorQueueName, failureContext.TransactionContext);
        }
    }
}
