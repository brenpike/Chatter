using System;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using FluentAssertions;
using Chatter.Testing.Core.Integration;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Deadletter→DLQ integration proof for the RabbitMQ integration harness, PROVEN ON BOTH queue types per
    // ADR-0001. The SYSTEM UNDER TEST is Chatter's Max-Receives-Exceeded deadletter path: when
    // RecordingMessageHandler<T> throws and the resolved delivery count has reached MaxReceiveAttempts,
    // BrokeredMessageReceiver routes to RabbitMqReceiver.DeadletterMessageAsync, which republishes the original
    // body to the attribute-declared DeadLetterQueuePath (publisher-confirmed) with the failure headers merged,
    // then acks the original. The test reads the dead-letter queue directly at the test edge (BasicGet) and
    // asserts the republished envelope's failure headers.
    //
    // THE QUEUE-TYPE PROOF (the count SOURCE differs per ADR-0001, both drive the SAME deadletter outcome):
    //
    //   Quorum (maxReceiveAttempts: 1): the count comes from the broker's NATIVE x-delivery-count. On the FIRST
    //   delivery attempts = x-delivery-count(0) + 1 = 1, which is >= MaxReceiveAttempts(1), so the message
    //   deadletters IMMEDIATELY on the first throw — no redelivery loop. Proves the native-counter strategy.
    //
    //   Classic (maxReceiveAttempts: 1): the count comes from the adapter's OWN x-chatter-delivery-count header.
    //   On the FIRST delivery the header is absent so attempts = 0, which is NOT >= 1, so NackMessageAsync runs
    //   the CLASSIC REPUBLISH: it republishes to the work queue with x-chatter-delivery-count = 1 (confirmed),
    //   then acks the original. On the redelivery attempts = 1 >= 1, so it deadletters. Proves the
    //   header-stamped republish counter advancing across a redelivery.
    //
    // FAILURE-HEADER KEYS ASSERTED (merged onto the deadletter envelope's AMQP headers): the adapter stamps
    // MessageContext.FailureDetails (deadLetterReason) + MessageContext.FailureDescription
    // (deadLetterErrorDescription = the handler exception ToString), and the inbound headers carry the
    // send-time MessageContext.InfrastructureType. ReceiveAttempts is NOT carried in the raw AMQP headers (it is
    // stamped on the inbound MessageBrokerContext at receive time, not on the wire), so it is not asserted here.
    //
    // The facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green. Mirrors the SQL Service Broker SsbDeadLetterTests for harness setup.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqDeadLetterTests
    {
        private static readonly TimeSpan DeadLetterWait = TimeSpan.FromSeconds(30);

        private readonly RabbitMqFixture _fixture;

        public RabbitMqDeadLetterTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        // Distinct command type per queue type so the two scenarios' queue state is independent.
        public sealed class QuorumDeadLetterCommand : ICommand
        {
            public string Marker { get; set; }
        }

        public sealed class ClassicDeadLetterCommand : ICommand
        {
            public string Marker { get; set; }
        }

        // Quorum: the native x-delivery-count drives attempts; maxReceiveAttempts:1 deadletters on the first
        // throw.
        [RequiresDockerFact]
        public Task QuorumQueueDeadlettersOnMaxReceivesViaNativeDeliveryCount()
            => RunDeadLetterScenarioAsync<QuorumDeadLetterCommand>(
                QueueType.Quorum,
                () => new QuorumDeadLetterCommand { Marker = "quorum-deadletter" });

        // Classic: the adapter's x-chatter-delivery-count republish counter drives attempts; maxReceiveAttempts:1
        // deadletters on the redelivery after one header-stamped republish.
        [RequiresDockerFact]
        public Task ClassicQueueDeadlettersOnMaxReceivesViaRepublishCounter()
            => RunDeadLetterScenarioAsync<ClassicDeadLetterCommand>(
                QueueType.Classic,
                () => new ClassicDeadLetterCommand { Marker = "classic-deadletter" });

        // Shared scenario body driven for BOTH queue types: a throwing handler at MaxReceiveAttempts=1 must cause
        // DeadletterMessageAsync to republish the failed message to the dead-letter queue with the failure
        // headers merged. The ONLY difference between the runs is the QueueType (and therefore the count source),
        // which the per-fact callers supply — the outcome assertions are identical.
        private async Task RunDeadLetterScenarioAsync<TMessage>(QueueType queueType, Func<TMessage> commandFactory)
            where TMessage : class, ICommand
        {
            var suffix = queueType == QueueType.Quorum ? "dl_quorum" : "dl_classic";
            var set = RabbitMqTopology.CreateSet(suffix, queueType);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                queueType,
                // maxReceiveAttempts: 1 is the deadletter trigger lever. Quorum trips on the first delivery
                // (attempts = native count + 1 = 1); Classic trips on the redelivery after one republish
                // (attempts = x-chatter-delivery-count = 1).
                rmq => rmq.AddQueueReceiver<TMessage>(
                    set.WorkQueueName,
                    deadLetterQueuePath: set.DeadLetterQueueName,
                    maxReceiveAttempts: 1),
                typeof(TMessage));
            try
            {
                await harness.StartAsync();

                // Arm the throw BEFORE sending so the handler throws on every delivery, driving the message past
                // Max Receives to the deadletter republish.
                harness.GetSignal<TMessage>().ThrowOnHandle =
                    () => new InvalidOperationException("deadletter-test forced throw");

                await harness.SendToQueueAsync(commandFactory(), set.WorkQueueName);

                // The handler must be invoked at least once before deadlettering can occur. WaitForHandledAsync
                // throws TimeoutException if the handler is never reached, failing fast instead of hanging.
                await harness.WaitForHandledAsync<TMessage>(DeadLetterWait);

                // Read the dead-letter queue at the test edge, polling until the republished envelope arrives.
                var deadLettered = await DeadLetterQueueReader.ReceiveAsync(
                    _fixture.GetAmqpConnectionString(), set.DeadLetterQueueName, DeadLetterWait);

                deadLettered.Should().NotBeNull(
                    "the throwing handler at MaxReceiveAttempts=1 must cause DeadletterMessageAsync to republish " +
                    "the failed message to the dead-letter queue");

                // The deadletter envelope's AMQP headers carry the failure metadata DeadletterMessageAsync stamps.
                deadLettered.Headers.Should().ContainKey(MessageContext.FailureDescription,
                    "DeadletterMessageAsync merges FailureDescription (the handler exception) into the republished headers");
                deadLettered.Headers[MessageContext.FailureDescription]
                    .Should().Contain("deadletter-test forced throw",
                        "the deadletter error description carries the originating handler exception");

                deadLettered.Headers.Should().ContainKey(MessageContext.FailureDetails,
                    "DeadletterMessageAsync merges FailureDetails (the deadletter reason) into the republished headers");

                // InfrastructureType was stamped on send and carried through the inbound headers into the
                // republished envelope, identifying the RabbitMQ receiver as the deadletter origin.
                deadLettered.Headers.Should().ContainKey(MessageContext.InfrastructureType);
                deadLettered.Headers[MessageContext.InfrastructureType]
                    .Should().Be(RabbitMqMessageContext.InfrastructureType);
            }
            finally
            {
                harness.GetSignal<TMessage>().ThrowOnHandle = null;
                await harness.DisposeAsync();
            }
        }
    }
}
