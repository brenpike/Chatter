using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using FluentAssertions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // TERMINAL, SURGICAL receiver-teardown integration proof against a REAL broker (closes PR #194 r3407966808;
    // ADR-0005). The SYSTEM UNDER TEST is the production RabbitMqConnectionSource, which OWNS the receive channel +
    // consumer lifecycle (ADR-0002/0003). StopReceivingAsync must, under the receive gate:
    //   - cancel the registered AMQP consumer and tear down the RECEIVE CHANNEL, so no further deliveries reach the
    //     cancelled consumer and a prefetched-but-unacked delivery is REQUEUED by the broker (it remains on the
    //     queue rather than being stranded on a dead channel);
    //   - leave the IConnection and the publish pool intact, so the sender keeps publishing after the receiver stops
    //     (the source is a process singleton shared with the sender).
    //
    // Driven at the SOURCE level (not the full Chatter pump) so the surgical StopReceivingAsync contract is observed
    // directly: a counting consumer proves cancellation (no post-stop delivery), a fresh channel on the SAME source
    // connection proves the unacked delivery was requeued, and AcquirePublishChannelAsync proves the shared
    // connection survives the stop.
    //
    // Gated by [RequiresDockerFact] + Category=Integration: SKIPPED when Docker is absent so a plain `dotnet test`
    // stays green; the nightly RabbitMQ CI lane runs it for real.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqReceiverTeardownTests
    {
        private static readonly TimeSpan DeliveryWait = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(3);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqReceiverTeardownTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        [RequiresDockerFact]
        public async Task StopReceivingCancelsConsumerRequeuesUnackedAndKeepsSenderPublishing()
        {
            var amqpUri = _fixture.GetAmqpConnectionString();
            var set = RabbitMqTopology.CreateSet("teardown", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(amqpUri, set, CancellationToken.None);

            var source = new RabbitMqConnectionSource(new RabbitMqOptions(uri: amqpUri, queueType: QueueType.Quorum));

            var deliveryCount = 0;
            var firstDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                // Register a counting consumer that NEVER acks, so the delivery stays prefetched-but-unacked on the
                // receive channel — the exact state a stop must requeue rather than strand.
                await source.StartReceivingAsync(async (channel, epoch, ct) =>
                {
                    await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: ct)
                        .ConfigureAwait(false);

                    var consumer = new AsyncEventingBasicConsumer(channel);
                    consumer.ReceivedAsync += (_, _) =>
                    {
                        Interlocked.Increment(ref deliveryCount);
                        firstDelivered.TrySetResult();
                        return Task.CompletedTask;
                    };

                    return await channel.BasicConsumeAsync(queue: set.WorkQueueName,
                                                           autoAck: false,
                                                           consumerTag: string.Empty,
                                                           noLocal: false,
                                                           exclusive: false,
                                                           arguments: null,
                                                           consumer: consumer,
                                                           cancellationToken: ct).ConfigureAwait(false);
                }, CancellationToken.None);

                // Publish a message through the SOURCE's publish pool (the same path the sender uses) and wait for the
                // counting consumer to receive it (left unacked).
                await PublishAsync(source, set.WorkQueueName, "teardown-unacked", CancellationToken.None);

                var delivered = await Task.WhenAny(firstDelivered.Task, Task.Delay(DeliveryWait));
                delivered.Should().Be(firstDelivered.Task, "the consumer must receive the published message before the stop");
                var countAtStop = Volatile.Read(ref deliveryCount);

                // SURGICAL TERMINAL STOP: cancel the consumer + tear down the receive channel only.
                await source.StopReceivingAsync(CancellationToken.None);

                // The sender's publish path must still work after the stop (shared connection intact). Publish a
                // SECOND message; it must NOT reach the cancelled consumer.
                await PublishAsync(source, set.WorkQueueName, "teardown-after-stop", CancellationToken.None);

                // No further deliveries after the stop: the consumer was cancelled and the receive channel torn down.
                await Task.Delay(QuietWindow);
                Volatile.Read(ref deliveryCount).Should().Be(countAtStop,
                    "the cancelled consumer must receive no further deliveries after StopReceivingAsync");

                // Prove the unacked first delivery was REQUEUED (not stranded) AND the second publish landed: a fresh
                // channel on the SAME source connection drains BOTH messages off the queue via BasicGet. Acquiring
                // that channel through AcquirePublishChannelAsync also proves the shared connection survived the stop.
                await using var rental = await source.AcquirePublishChannelAsync(CancellationToken.None);
                var drained = await DrainQueueAsync(rental.Channel, set.WorkQueueName, expected: 2, CancellationToken.None);

                drained.Should().BeGreaterThanOrEqualTo(2,
                    "the prefetched-but-unacked delivery must be requeued by the broker on consumer cancel / channel " +
                    "teardown (so it remains on the queue), and the post-stop publish must have landed — both are " +
                    "drainable on a fresh channel from the SAME source connection, proving the connection survived the stop");
            }
            finally
            {
                await source.DisposeAsync();
            }
        }

        private static async Task PublishAsync(RabbitMqConnectionSource source, string queue, string body, CancellationToken cancellationToken)
        {
            await using var rental = await source.AcquirePublishChannelAsync(cancellationToken).ConfigureAwait(false);
            await rental.Channel.BasicPublishAsync(exchange: string.Empty,
                                                   routingKey: queue,
                                                   mandatory: true,
                                                   basicProperties: new BasicProperties(),
                                                   body: new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)),
                                                   cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Drains up to `expected` messages off the queue via BasicGet (autoAck), retrying briefly so a just-requeued
        // delivery has time to reappear. Returns the count actually drained.
        private static async Task<int> DrainQueueAsync(IChannel channel, string queue, int expected, CancellationToken cancellationToken)
        {
            var drained = 0;
            var deadline = DateTime.UtcNow + DeliveryWait;
            while (drained < expected && DateTime.UtcNow < deadline)
            {
                var result = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                drained++;
            }

            return drained;
        }
    }
}
