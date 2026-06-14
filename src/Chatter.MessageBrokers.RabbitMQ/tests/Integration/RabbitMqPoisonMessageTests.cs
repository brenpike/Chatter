using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using RabbitMQ.Client;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Poison-message integration proof for the RabbitMQ integration harness, PROVEN ON BOTH queue types per
    // ADR-0001. The SYSTEM UNDER TEST is core behavior C8: a body that cannot DESERIALIZE to TMessage is
    // deadlettered IMMEDIATELY (no MaxReceiveAttempts climb) and the handler is NEVER invoked.
    //
    // WHY THIS IS DISTINCT FROM RabbitMqDeadLetterTests: that suite proves the Max-Receives-Exceeded deadletter,
    // which is the GENERIC processing-error ladder — the handler is invoked, throws, and only after the resolved
    // delivery count reaches MaxReceiveAttempts does BrokeredMessageReceiver route to DeadletterMessageAsync. C8
    // is the OTHER deadletter path: ProcessMessageAsync calls inboundMessage.GetMessageFromBody<TMessage>() BEFORE
    // any handler dispatch; when the body cannot materialize to TMessage that throws, which the receiver wraps in a
    // PoisonedMessageException, and the worker's catch (PoisonedMessageException) branch deadletters at once via
    // TryDeadletterWithRecoveryAsync — bypassing the delivery-count probe entirely. So the poison message
    // deadletters on the FIRST delivery REGARDLESS of MaxReceiveAttempts, and the handler is never reached.
    //
    // INJECTING THE POISON: Chatter's own Send path would serialize a VALID JSON envelope, which deserializes
    // fine. To force the deserialize to throw we publish a malformed body DIRECTLY at the raw RabbitMQ.Client edge
    // (reusing RabbitMqFixture's AMQP URI + RabbitMqTopology's declared work queue), bypassing Chatter's serializer.
    // The RabbitMqBodyConverter deserializes via JsonSerializer.Deserialize<TMessage>(UTF8 body); a non-JSON byte
    // payload throws a JsonException there, which surfaces as the PoisonedMessageException. We deliberately omit the
    // AMQP content-type so the receiver falls back to the configured (JSON) MessageBodyType converter, exercising
    // the same deserialize the production receive path uses.
    //
    // THE NO-RETRY-CLIMB PROOF: maxReceiveAttempts is set HIGH (5). A Max-Receives-Exceeded deadletter would
    // therefore require the message to be delivered (and the handler invoked) up to 5 times before deadlettering.
    // The poison path instead deadletters on the FIRST delivery with the handler never invoked, so:
    //   - the DLQ envelope arrives promptly (first delivery), AND
    //   - RecordingMessageHandler<TMessage>.InvocationCount stays 0 (the handler was never reached).
    // Together these assert at the Chatter level (DLQ envelope + handler count) that there was no retry climb — it
    // did not take MaxReceiveAttempts deliveries to deadletter — rather than reading any raw SDK delivery counter.
    //
    // The DLQ envelope's FailureDescription carries the deadletter error description, which for the poison path is
    // the PoisonedMessageException.ToString() — asserting it names the poison exception confirms the deadletter
    // came from the deserialize-failure path, not the generic handler-throw ladder.
    //
    // The facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green. Mirrors RabbitMqDeadLetterTests for harness setup.
    [Trait("Category", "Integration")]
    [Collection(RabbitMqCollection.Name)]
    public class RabbitMqPoisonMessageTests
    {
        private static readonly TimeSpan PoisonWait = TimeSpan.FromSeconds(30);

        // High enough that a Max-Receives-Exceeded deadletter would require several handler invocations; the poison
        // path must deadletter on the FIRST delivery with zero invocations regardless of this value.
        private const int MaxReceiveAttempts = 5;

        private readonly RabbitMqFixture _fixture;

        public RabbitMqPoisonMessageTests(RabbitMqFixture fixture)
            => _fixture = fixture;

        // Distinct command type per queue type so the two scenarios' queue state is independent.
        public sealed class QuorumPoisonCommand : ICommand
        {
            public string Marker { get; set; }
        }

        public sealed class ClassicPoisonCommand : ICommand
        {
            public string Marker { get; set; }
        }

        // Quorum: a poison body deadletters immediately on the native-delivery-count strategy without the handler
        // ever being invoked, regardless of maxReceiveAttempts.
        [RequiresDockerFact]
        public Task QuorumQueueDeadlettersPoisonMessageImmediatelyWithoutInvokingHandler()
            => RunPoisonScenarioAsync<QuorumPoisonCommand>(QueueType.Quorum);

        // Classic: a poison body deadletters immediately on the republish-counter strategy without the handler ever
        // being invoked, regardless of maxReceiveAttempts.
        [RequiresDockerFact]
        public Task ClassicQueueDeadlettersPoisonMessageImmediatelyWithoutInvokingHandler()
            => RunPoisonScenarioAsync<ClassicPoisonCommand>(QueueType.Classic);

        // Shared scenario body driven for BOTH queue types: a malformed body published at the raw edge must cause
        // the receive-side deserialize to throw PoisonedMessageException, which deadletters on the first delivery
        // WITHOUT invoking the handler. The ONLY difference between the runs is the QueueType — the outcome
        // assertions are identical, proving C8 holds independent of the delivery-count strategy.
        private async Task RunPoisonScenarioAsync<TMessage>(QueueType queueType)
            where TMessage : class, ICommand
        {
            var suffix = queueType == QueueType.Quorum ? "poison_quorum" : "poison_classic";
            var set = RabbitMqTopology.CreateSet(suffix, queueType);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                queueType,
                // maxReceiveAttempts is set HIGH so the ONLY way a deadletter happens on the first delivery (with
                // zero handler invocations) is the poison/deserialize-failure path, not a Max-Receives climb.
                rmq => rmq.AddQueueReceiver<TMessage>(
                    set.WorkQueueName,
                    deadLetterQueuePath: set.DeadLetterQueueName,
                    maxReceiveAttempts: MaxReceiveAttempts),
                typeof(TMessage));
            try
            {
                await harness.StartAsync();

                // Inject the POISON: publish a malformed (non-JSON) body DIRECTLY at the raw RabbitMQ.Client edge,
                // bypassing Chatter's serializer, so the receive-side JsonSerializer.Deserialize<TMessage> throws and
                // the receiver wraps it in a PoisonedMessageException. No content-type is stamped so the receiver
                // falls back to the configured JSON body converter — the same deserialize the production path runs.
                await PublishPoisonBodyAsync(
                    set.WorkQueueName,
                    Encoding.UTF8.GetBytes("this-is-not-a-valid-json-envelope"));

                // The poison message must land in the DLQ on the FIRST delivery (the deserialize failure deadletters
                // before any handler dispatch). ReceiveAsync polls until the republished envelope arrives or fails
                // fast via TimeoutException-equivalent null on the deadline.
                var deadLettered = await DeadLetterQueueReader.ReceiveAsync(
                    _fixture.GetAmqpConnectionString(), set.DeadLetterQueueName, PoisonWait);

                deadLettered.Should().NotBeNull(
                    "an undeserializable body must be deadlettered immediately via the PoisonedMessageException path, " +
                    "republished to the dead-letter queue on the first delivery");

                // The deadletter error description carries the deadletter-time exception ToString(); for the poison
                // path that is the PoisonedMessageException, confirming the deadletter came from the deserialize
                // failure rather than the generic handler-throw ladder.
                deadLettered.Headers.Should().ContainKey(MessageContext.FailureDescription,
                    "DeadletterMessageAsync merges FailureDescription (the deadletter error description) into the republished headers");
                deadLettered.Headers[MessageContext.FailureDescription]
                    .Should().Contain(nameof(Chatter.MessageBrokers.Exceptions.PoisonedMessageException),
                        "the poison deadletter's error description is the PoisonedMessageException, identifying the deserialize-failure path");

                // FailureDetails carries the poison deadletter reason. Unlike the InfrastructureType header — which
                // is stamped by Chatter's SENDER and is therefore absent here because the poison body was published
                // RAW (bypassing the sender) — the failure overrides are merged by DeadletterMessageAsync itself, so
                // they are present on the republished envelope regardless of how the original was published.
                deadLettered.Headers.Should().ContainKey(MessageContext.FailureDetails,
                    "DeadletterMessageAsync merges FailureDetails (the deadletter reason) into the republished headers");
                deadLettered.Headers[MessageContext.FailureDetails]
                    .Should().Be("Poisoned message received",
                        "the poison deadletter reason identifies the PoisonedMessageException path, not the Max-Receives ladder");

                // C8 CORE ASSERTION: the handler was NEVER invoked. The poison deadletter happens in the
                // catch (PoisonedMessageException) branch BEFORE any handler dispatch, so a correct adapter leaves
                // the RecordingMessageHandler<TMessage> untouched. A non-zero count here would mean the body reached
                // the handler (i.e. it deserialized, contradicting the poison premise) or that the deadletter came
                // from the Max-Receives ladder after handler throws — either of which violates C8.
                harness.GetSignal<TMessage>().InvocationCount.Should().Be(0,
                    "a poison/undeserializable message is deadlettered before dispatch, so the handler is never invoked");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // Publishes a raw body DIRECTLY to the work queue via RabbitMQ.Client, bypassing Chatter's send/serialize
        // path entirely so the body is whatever bytes the caller supplies (here, a non-JSON payload the receive-side
        // deserialize cannot bind to TMessage). Default-exchange convention: routing key == work queue name, matching
        // how the harness's own Send and the adapter's republish address the work queue. Persistent so the durable
        // work queue retains it. No content-type is set so the receiver uses the configured JSON body converter.
        private async Task PublishPoisonBodyAsync(string workQueueName, byte[] body)
        {
            using var operationCts = new CancellationTokenSource(PoisonWait);
            var token = operationCts.Token;

            var factory = new ConnectionFactory { Uri = new Uri(_fixture.GetAmqpConnectionString()) };
            await using var connection = await factory.CreateConnectionAsync(token).ConfigureAwait(false);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: token).ConfigureAwait(false);

            var properties = new BasicProperties { Persistent = true };

            await channel.BasicPublishAsync(exchange: string.Empty,
                                            routingKey: workQueueName,
                                            mandatory: true,
                                            basicProperties: properties,
                                            body: new ReadOnlyMemory<byte>(body),
                                            cancellationToken: token).ConfigureAwait(false);
        }
    }
}
