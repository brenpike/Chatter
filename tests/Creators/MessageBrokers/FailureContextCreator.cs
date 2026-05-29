using System;
using Chatter.MessageBrokers.Context;

namespace Chatter.Testing.Core.Creators.MessageBrokers
{
    /// <summary>
    /// Builds a default <see cref="FailureContext"/> for characterization tests of the recovery dispatchers.
    /// The production <see cref="FailureContext"/> constructor requires a non-empty failure description, so this
    /// creator always supplies one. Tests that need specific Inbound/ErrorQueueName/TransactionContext values
    /// construct <see cref="FailureContext"/> directly because those values are load-bearing in the assertion.
    /// </summary>
    public class FailureContextCreator : Creator<FailureContext>
    {
        public FailureContextCreator(INewContext newContext, FailureContext creation = default)
            : base(newContext, creation)
            => Creation = new FailureContext(
                inbound: null,
                errorQueueName: "error-queue",
                failureDescription: "failure-description",
                failure: new InvalidOperationException("boom"),
                deliveryCount: 1,
                transactionContext: null);
    }
}
