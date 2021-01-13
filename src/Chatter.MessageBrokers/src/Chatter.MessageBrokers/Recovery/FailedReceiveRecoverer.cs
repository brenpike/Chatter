using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    public class FailedReceiveRecoverer : IFailedReceiveRecoverer
    {
        private readonly RecoveryOptions _options;
        private readonly IDelayedRecoveryStrategy _delayedRecovery;
        private readonly IRecoveryAction _recoveryAction;

        public FailedReceiveRecoverer(RecoveryOptions options, IDelayedRecoveryStrategy delayedRecovery, IRecoveryAction recoveryAction)
        {
            _options = options;
            _delayedRecovery = delayedRecovery;
            _recoveryAction = recoveryAction;
        }

        public async Task<RecoveryState> Execute(FailureContext failureContext)
        {
            await _delayedRecovery.Execute(failureContext).ConfigureAwait(false);

            if (failureContext.DeliveryCount >= _options.MaxRetryAttempts)
            {
                return await _recoveryAction.Execute(failureContext).ConfigureAwait(false);
            }

            return RecoveryState.Retrying;
        }
    }
}
