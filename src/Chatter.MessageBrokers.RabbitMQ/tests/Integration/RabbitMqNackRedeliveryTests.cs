using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
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

        // Distinct command type so this test's queue state is independent of the other integration tests in the
        // collection.
        public sealed class NackRedeliveryCommand : ICommand
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
    }
}
