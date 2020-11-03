using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    public class ErrorQueueDispatcher : IRecoveryAction
    {
        private readonly IForwardMessages _forwardMessages;

        public ErrorQueueDispatcher(IForwardMessages forwardMessages)
            => _forwardMessages = forwardMessages;

        public async Task<RecoveryState> Execute(FailureContext failureContext)
        {
            if (string.IsNullOrWhiteSpace(failureContext.ErrorQueueName))
            {
                return RecoveryState.DeadLetter;
            }

            await _forwardMessages.Route(failureContext.Inbound, failureContext.ErrorQueueName, failureContext.TransactionContext).ConfigureAwait(false);

            return RecoveryState.RecoveryActionExecuted;
        }
    }
}
