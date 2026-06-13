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
    /// INVARIANT (closed-by-construction epoch lifecycle): the source OWNS the receive channel and
    /// consumer lifecycle. On every (re)creation of the receive channel — cold start, lazy recreate,
    /// and automatic recovery — the source, UNDER THE RECEIVE GATE and as ONE atomic event, recreates
    /// the channel, increments the epoch, and re-runs the registration delegate supplied to
    /// <see cref="StartReceivingAsync"/>. The registration delegate is the ONLY code that stamps an
    /// epoch onto deliveries, and it always runs AFTER the epoch bump with the freshly-bumped epoch, so
    /// a delivery's stamped epoch ALWAYS equals the epoch of the session that delivered it. Topology
    /// (consumer) auto-recovery is therefore disabled on the connection: re-registration is owned here,
    /// not by the client, which is what makes the stamped epoch race-free against recovery.
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
        /// Hands the source the consume-registration delegate it must run to register the push consumer on
        /// the receive channel, and runs it once immediately against the freshly-(re)created channel. The
        /// source STORES the delegate and re-runs it — UNDER THE RECEIVE GATE, after bumping the epoch — on
        /// every subsequent receive-channel (re)creation (lazy recreate and automatic recovery), so the
        /// consumer is always re-registered on the live channel and always closes over the current epoch.
        /// </summary>
        /// <remarks>
        /// INVARIANT: <paramref name="registerConsumer"/> is the ONLY code that stamps an epoch onto a
        /// delivery, and the source guarantees it runs AFTER every epoch bump with the freshly-bumped epoch
        /// passed as the <see cref="long"/> argument. Because the bump and the re-registration are the same
        /// gated event, the epoch stamped onto a delivery always equals the epoch of the session that
        /// delivered it — by construction, with no read/stamp race against recovery.
        /// </remarks>
        /// <param name="registerConsumer">
        /// Registers the push consumer on the supplied live <see cref="IChannel"/>, stamping the supplied
        /// epoch onto every delivery it buffers. Invoked under the receive gate on every (re)creation.
        /// </param>
        /// <param name="cancellationToken">A token to cancel acquisition of the gate / initial registration.</param>
        Task StartReceivingAsync(Func<IChannel, long, CancellationToken, Task> registerConsumer,
                                 CancellationToken cancellationToken);

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
