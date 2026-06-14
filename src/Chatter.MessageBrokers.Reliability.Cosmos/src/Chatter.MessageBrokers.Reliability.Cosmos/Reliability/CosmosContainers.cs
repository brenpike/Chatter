using Microsoft.Azure.Cosmos;
using System;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// DI holder for the application-injected document (aggregate) container. The provider creates no container — it
    /// binds the instance the application supplies. Two distinct <see cref="Container"/> instances (document and
    /// change-feed lease) must be disambiguated in DI; a typed holder per role does that without resorting to keyed
    /// services.
    /// </summary>
    public sealed class DocumentContainer
    {
        public DocumentContainer(Container container)
            => Container = container ?? throw new ArgumentNullException(nameof(container));

        /// <summary>
        /// The application-owned container the aggregate, co-resident outbox doc, and co-resident inbox marker live in.
        /// </summary>
        public Container Container { get; }
    }

    /// <summary>
    /// DI holder for the application-injected change-feed lease container. Registered in #218 but unused until the
    /// change-feed relay (#222) consumes it. The provider creates no container — it binds the supplied instance.
    /// </summary>
    public sealed class LeaseContainer
    {
        public LeaseContainer(Container container)
            => Container = container ?? throw new ArgumentNullException(nameof(container));

        /// <summary>
        /// The application-owned change-feed lease container.
        /// </summary>
        public Container Container { get; }
    }
}
