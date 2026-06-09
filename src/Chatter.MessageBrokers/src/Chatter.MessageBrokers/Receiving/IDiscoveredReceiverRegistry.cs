using System.Collections.Generic;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// An infrastructure-agnostic registry that RETAINS the live <see cref="ReceiverOptions"/> instance for every
    /// receiver discovered or registered during Chatter message broker configuration. Registered as a singleton.
    /// </summary>
    /// <remarks>
    /// The SAME <see cref="ReceiverOptions"/> instance captured into a receiver's hosted-service closure is retained
    /// here, so an infrastructure package may read these options after the broker is built (e.g. to enforce
    /// infrastructure-specific startup guards) and may mutate a retained instance (e.g. to stamp an
    /// infrastructure-resolved value) such that the mutation is visible when the receiver reads its options at init.
    /// Core holds no infrastructure concept; any infrastructure-specific interpretation (entity-name inference,
    /// <see cref="ReceiverOptions.InfrastructureType"/> matching) lives in the infrastructure package that consumes
    /// this registry.
    /// </remarks>
    public interface IDiscoveredReceiverRegistry
    {
        /// <summary>
        /// Retains the live <see cref="ReceiverOptions"/> instance for a discovered or registered receiver.
        /// </summary>
        void Register(ReceiverOptions options);

        /// <summary>
        /// The retained live <see cref="ReceiverOptions"/> instances for every discovered or registered receiver.
        /// </summary>
        IReadOnlyCollection<ReceiverOptions> DiscoveredReceivers { get; }
    }
}
