using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using FluentAssertions;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Nack→redelivery integration proof for the RabbitMQ integration harness. The SYSTEM UNDER TEST is
    // Chatter's nack path on a Quorum queue: when RecordingMessageHandler<T> throws and ReceiveAttempts has NOT
    // reached MaxReceiveAttempts, BrokeredMessageReceiver routes to RabbitMqReceiver.NackMessageAsync, which on
    // a quorum queue issues BasicNack(requeue: true) so the broker increments the native x-delivery-count and
    // redelivers. The test asserts (a) the handler is invoked at least twice (proving redelivery happened), and
    // (b) the ReceiveAttempts stamp climbs across deliveries (proving attempts = native x-delivery-count + 1
    // advances on each redelivery). Mirrors the SQL Service Broker SsbNackRedeliveryTests.
    //
    // ANTI-INFINITE-LOOP: ThrowOnHandle is flipped to null as soon as >= 2 invocations are observed so the
    // message finally acks before DisposeAsync drains the pump. MaxReceiveAttempts is left at the default (10),
    // well above the 2 redeliveries the test drives, so the message nacks/redelivers (never deadletters) in the
    // window under test.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqNackRedeliveryTests
    {
        private static readonly TimeSpan RedeliveryWait = TimeSpan.FromSeconds(30);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqNackRedeliveryTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        private static readonly TimeSpan TtlPropagationWait = TimeSpan.FromSeconds(30);

        // Distinct command type so this test's queue state is independent of the other integration tests in the
        // collection.
        public sealed class NackRedeliveryCommand : ICommand
        {
            public string Marker { get; set; }
        }

        // Distinct command type for the classic TTL-propagation scenario so its queue state is independent.
        public sealed class ClassicTtlRedeliveryCommand : ICommand
        {
            public string Marker { get; set; }
        }

        // Nack→redelivery: when the handler throws, NackMessageAsync issues BasicNack(requeue:true) on the
        // quorum queue so the broker redelivers and increments x-delivery-count. Assert invocation count >= 2
        // (at least one redelivery) and that ReceiveAttempts climbs across successive deliveries.
        [RequiresDockerFact]
        public async Task ThrowingHandlerCausesRedeliveryAndClimbingReceiveAttempts()
        {
            var set = RabbitMqTopology.CreateSet("nack", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<NackRedeliveryCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(NackRedeliveryCommand));
            try
            {
                await harness.StartAsync();

                // Arm the throw BEFORE sending so the handler throws on the very first delivery. ThrowOnHandle is
                // Func<Exception>: a fresh instance per invocation matches the contract RecordingMessageHandler<T>
                // uses (it calls thrower() and throws the result).
                harness.GetSignal<NackRedeliveryCommand>().ThrowOnHandle =
                    () => new InvalidOperationException("nack-redelivery-test forced throw");

                await harness.SendToQueueAsync(new NackRedeliveryCommand { Marker = "nack-redelivery" }, set.WorkQueueName);

                // Wait until at least 2 handler invocations have been observed (first delivery + at least one
                // redelivery). WaitForInvocationCountAsync returns the last observed count, which may be below
                // minCount when the timeout elapses — the assertion below catches that case explicitly.
                var observedCount = await harness.WaitForInvocationCountAsync<NackRedeliveryCommand>(
                    minCount: 2, RedeliveryWait);

                // ANTI-INFINITE-LOOP: stop throwing so the message is acked on the next receive before
                // DisposeAsync drains the pump.
                harness.GetSignal<NackRedeliveryCommand>().ThrowOnHandle = null;

                observedCount.Should().BeGreaterThanOrEqualTo(2,
                    "the handler must be invoked at least twice: once for the original delivery and once for the " +
                    "redelivery after NackMessageAsync requeued the message and the broker redelivered it");

                // ReceiveAttempts must climb: on a quorum queue attempts = native x-delivery-count + 1, which the
                // broker advances on each redelivery. Capture the attempt stamp from each recorded invocation and
                // assert the maximum observed value exceeds 1.
                var records = harness.GetSignal<NackRedeliveryCommand>().Records.ToList();
                var maxAttempts = records
                    .Where(r => r.Context?.BrokeredMessage?.MessageContext?.ContainsKey(MessageContext.ReceiveAttempts) == true)
                    .Select(r => Convert.ToInt32(r.Context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts]))
                    .DefaultIfEmpty(0)
                    .Max();

                maxAttempts.Should().BeGreaterThan(1,
                    "the quorum native x-delivery-count must advance on each redelivery, so ReceiveAttempts " +
                    "(x-delivery-count + 1) must exceed 1 after at least one redelivery");
            }
            finally
            {
                harness.GetSignal<NackRedeliveryCommand>().ThrowOnHandle = null;
                await harness.DisposeAsync();
            }
        }

        // TTL-PROPAGATION proof against a REAL broker (closes the native-property-propagation root-cluster): a
        // CLASSIC-queue message published WithTimeToLive must keep its per-message TTL across the classic
        // nack-redelivery republish. On a classic queue NackMessageAsync rebuilds the outbound message via the
        // shared BuildRepublishProperties helper, which now re-applies the carried native Expiration; before the
        // fix the republish rebuilt BasicProperties from scratch and DROPPED the delivered native Expiration, so a
        // classic-queue message lost its TTL on the first nack-redelivery (reviewer thread r3407577438).
        //
        // The redelivered copy's native Expiration is observed race-free at the handler edge: BufferDeliveryAsync
        // captures the delivered native Expiration onto the ReceivedMessage, which the receiver includes in the
        // MessageBrokerContext container, so the handler reads it directly off the SECOND (redelivered) invocation
        // — no competing BasicGet against the pump-consumed work queue.
        //
        // ANTI-INFINITE-LOOP: ThrowOnHandle is flipped to null as soon as the first invocation is observed so the
        // redelivered copy is acked before DisposeAsync drains the pump. MaxReceiveAttempts is left at the default
        // (10), well above the single redelivery the test drives, so the message nacks/redelivers (never
        // deadletters) in the window under test.
        [RequiresDockerFact]
        public async Task ClassicQueueRedeliveryPreservesNativeExpiration()
        {
            // 5 minutes in ms: long enough that the message does not expire before the in-process pump redelivers
            // it, so the surviving TTL is asserted on the redelivered copy.
            var timeToLive = TimeSpan.FromMinutes(5);
            var expectedExpiration = ((long)timeToLive.TotalMilliseconds).ToString();

            var set = RabbitMqTopology.CreateSet("ttlredelivery", QueueType.Classic);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Classic,
                rmq => rmq.AddQueueReceiver<ClassicTtlRedeliveryCommand>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(ClassicTtlRedeliveryCommand));
            try
            {
                await harness.StartAsync();

                // Throw on the FIRST delivery so NackMessageAsync runs the classic nack-republish (which must carry
                // the native Expiration), then redelivers. A fresh exception instance per invocation matches the
                // RecordingMessageHandler<T> contract.
                harness.GetSignal<ClassicTtlRedeliveryCommand>().ThrowOnHandle =
                    () => new InvalidOperationException("ttl-redelivery-test forced throw");

                await harness.SendToQueueWithTimeToLiveAndHeaderAsync(
                    new ClassicTtlRedeliveryCommand { Marker = "ttl-redelivery" },
                    set.WorkQueueName,
                    timeToLive,
                    customHeaderKey: "x-chatter-it-ttl",
                    customHeaderValue: "ttl-redelivery");

                var observedCount = await harness.WaitForInvocationCountAsync<ClassicTtlRedeliveryCommand>(
                    minCount: 2, TtlPropagationWait);

                // ANTI-INFINITE-LOOP: stop throwing so the redelivered copy is acked on its delivery.
                harness.GetSignal<ClassicTtlRedeliveryCommand>().ThrowOnHandle = null;

                observedCount.Should().BeGreaterThanOrEqualTo(2,
                    "the throwing handler must cause one classic nack-republish, so the message is delivered at " +
                    "least twice: the original delivery plus the redelivered republished copy");

                // The SECOND invocation is the redelivered republished copy. Its delivered native Expiration is
                // carried on the ReceivedMessage the receiver included in the MessageBrokerContext container.
                var records = harness.GetSignal<ClassicTtlRedeliveryCommand>().Records.ToList();
                records.Count.Should().BeGreaterThanOrEqualTo(2);

                var redelivered = records[1];
                redelivered.Context.Should().NotBeNull();
                redelivered.Context.Container.TryGet<ReceivedMessage>(out var receivedMessage).Should().BeTrue(
                    "the receiver includes the ReceivedMessage carrying the delivered native AMQP properties in the context container");

                receivedMessage.Expiration.Should().Be(expectedExpiration,
                    "the classic nack-redelivery republish must re-apply the delivered native Expiration so the " +
                    "per-message TTL survives the republish rather than being dropped");
            }
            finally
            {
                harness.GetSignal<ClassicTtlRedeliveryCommand>().ThrowOnHandle = null;
                await harness.DisposeAsync();
            }
        }
    }
}
