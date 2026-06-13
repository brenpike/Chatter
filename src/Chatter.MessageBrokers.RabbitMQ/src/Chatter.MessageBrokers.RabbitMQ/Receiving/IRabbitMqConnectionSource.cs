using System;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;

namespace Chatter.MessageBrokers.RabbitMQ.Receiving
{
    /// <summary>
    /// Adapter-owned seam over the AMQP connection and channels the RabbitMQ receiver and sender need.
    /// The production implementation (<see cref="RabbitMqConnectionSource"/>) is the sole place the
    /// configured connection settings become a live <see cref="IConnection"/>; an in-memory adapter
    /// substitutes it to pin receive/send/ack behavior in unit tests without a live broker.
    /// </summary>
    /// <remarks>
    /// AMQP channels are not thread-safe and a delivery tag is only valid on the exact channel that
    /// delivered it. The seam shapes around both constraints: receive operations (consume, ack, nack,
    /// deadletter republish) all run on ONE serialized channel funneled through an async gate via
    /// <see cref="RunOnReceiveChannelAsync{TResult}"/>, while publishing uses SEPARATE pooled channels
    /// rented through <see cref="AcquirePublishChannelAsync"/>.
    /// </remarks>
    public interface IRabbitMqConnectionSource : IAsyncDisposable
    {
        /// <summary>
        /// The epoch of the current receive channel. Monotonically increasing; bumped whenever the
        /// receive channel is (re)created or replaced (e.g. after automatic recovery). A buffered
        /// delivery carries the epoch of the channel that delivered it so a stale-epoch ack can be
        /// detected and made a no-op.
        /// </summary>
        long CurrentReceiveChannelEpoch { get; }

        /// <summary>
        /// Runs <paramref name="operation"/> against the single serialized receive channel while
        /// holding the async gate, so only one channel operation is in flight at a time. The operation
        /// receives the live <see cref="IChannel"/> and the epoch of that channel, allowing callers to
        /// stamp the epoch on consume and to compare against it on ack/nack/deadletter.
        /// </summary>
        /// <typeparam name="TResult">The result the operation produces.</typeparam>
        /// <param name="operation">The work to run on the receive channel under the gate.</param>
        /// <param name="cancellationToken">A token to cancel acquisition of the gate.</param>
        Task<TResult> RunOnReceiveChannelAsync<TResult>(Func<IChannel, long, Task<TResult>> operation,
                                                        CancellationToken cancellationToken);

        /// <summary>
        /// Acquires a publish channel from the separate publish pool. Pooled channels have publisher
        /// confirms enabled and are independent of the receive channel, so publishing never contends
        /// with the receive/ack gate. The returned rental must be disposed to return the channel to the
        /// pool.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel acquisition.</param>
        Task<RabbitMqPublishChannelRental> AcquirePublishChannelAsync(CancellationToken cancellationToken);
    }
}
