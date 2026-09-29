using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // Delivery counting against a RabbitMQ 4.3 broker. From 4.3 a quorum queue no longer counts a basic.nack with
    // requeue as a failed delivery, so the native x-delivery-count a quorum-queue Receiver reads its attempt number
    // from may never advance. These facts pin the outcome a Receiver owes on 4.3 regardless of how the count is
    // sourced:
    //
    //   - a quorum-queue message whose handler always throws reaches the dead-letter queue once maxReceiveAttempts
    //     is exhausted, republished by the Receiver itself (the FailureDetails header is present), rather than
    //     being redelivered indefinitely or dropped by the broker's own delivery-limit;
    //   - the ReceiveAttempts a handler observes climbs across quorum redeliveries (the 4.3 mirror of
    //     RabbitMqNackRedeliveryTests.ThrowingHandlerCausesRedeliveryAndClimbingReceiveAttempts);
    //   - a classic-queue message whose handler always throws still dead-letters, since the classic strategy counts
    //     with the Receiver's own x-chatter-delivery-count header and does not depend on the broker's counter.
    //
    // Each fact writes the ReceiveAttempts the Receiver resolved and the broker's redelivered flag for the first few
    // deliveries to the test output, so a failure shows whether the attempt count advanced across redeliveries. The
    // broker's native counters themselves are consumed at the receive boundary and are not observable here.
    //
    // ANTI-INFINITE-LOOP: every fact clears ThrowOnHandle in its finally block so a message still looping on the
    // work queue is acked on its next delivery before DisposeAsync drains the pump.
    [Trait("Category", "Integration")]
    [Collection(RabbitMq43Collection.Name)]
    public class RabbitMqDeliveryCountingOn43Tests
    {
        // Three attempts: low enough that a working count dead-letters within a second or two, and above 1 so the
        // quorum dead-letter needs the count to advance at least twice rather than tripping on the first delivery.
        private const int MaxReceiveAttempts = 3;

        // How many delivery snapshots each fact writes to the test output. A looping message can be delivered
        // thousands of times inside the wait, so the output is capped.
        private const int DeliverySnapshotLimit = 5;

        private static readonly TimeSpan DeadLetterWait = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan RedeliveryWait = TimeSpan.FromSeconds(20);

        private readonly RabbitMq43Fixture _fixture;
        private readonly ITestOutputHelper _output;

        public RabbitMqDeliveryCountingOn43Tests(RabbitMq43Fixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        // Distinct command type per scenario so each scenario's handler signal is independent.
        public sealed class QuorumMaxReceivesOn43Command : ICommand
        {
            public string Marker { get; set; }
        }

        public sealed class QuorumRedeliveryOn43Command : ICommand
        {
            public string Marker { get; set; }
        }

        public sealed class ClassicMaxReceivesOn43Command : ICommand
        {
            public string Marker { get; set; }
        }

        [RequiresDockerFact]
        public Task QuorumQueueDeadlettersOnMaxReceivesOn43()
            => RunMaxReceivesScenarioAsync<QuorumMaxReceivesOn43Command>(
                QueueType.Quorum,
                () => new QuorumMaxReceivesOn43Command { Marker = "quorum-maxreceives-43" });

        [RequiresDockerFact]
        public Task ClassicQueueDeadlettersOnMaxReceivesOn43()
            => RunMaxReceivesScenarioAsync<ClassicMaxReceivesOn43Command>(
                QueueType.Classic,
                () => new ClassicMaxReceivesOn43Command { Marker = "classic-maxreceives-43" });

        [RequiresDockerFact]
        public async Task QuorumQueueReceiveAttemptsClimbAcrossRedeliveriesOn43()
        {
            var set = RabbitMqTopology.CreateSet("v43_redelivery", QueueType.Quorum);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                QueueType.Quorum,
                rmq => rmq.AddQueueReceiver<QuorumRedeliveryOn43Command>(set.WorkQueueName, deadLetterQueuePath: set.DeadLetterQueueName),
                typeof(QuorumRedeliveryOn43Command));
            try
            {
                await harness.StartAsync();

                harness.GetSignal<QuorumRedeliveryOn43Command>().ThrowOnHandle =
                    () => new InvalidOperationException("v43-redelivery-test forced throw");

                await harness.SendToQueueAsync(new QuorumRedeliveryOn43Command { Marker = "quorum-redelivery-43" }, set.WorkQueueName);

                var observedCount = await harness.WaitForInvocationCountAsync<QuorumRedeliveryOn43Command>(
                    minCount: 2, RedeliveryWait);

                harness.GetSignal<QuorumRedeliveryOn43Command>().ThrowOnHandle = null;

                var records = harness.GetSignal<QuorumRedeliveryOn43Command>().Records.ToList();
                WriteDeliveredAttempts(records.Select(record => record.Context));

                observedCount.Should().BeGreaterThanOrEqualTo(2,
                    "the throwing handler must cause at least one redelivery of the nacked quorum message");

                var maxAttempts = records
                    .Where(record => record.Context?.BrokeredMessage?.MessageContext?.ContainsKey(MessageContext.ReceiveAttempts) == true)
                    .Select(record => Convert.ToInt32(record.Context.BrokeredMessage.MessageContext[MessageContext.ReceiveAttempts]))
                    .DefaultIfEmpty(0)
                    .Max();

                maxAttempts.Should().BeGreaterThan(1,
                    "ReceiveAttempts must advance on each quorum redelivery on RabbitMQ 4.3, so it must exceed 1 " +
                    "after at least one redelivery");
            }
            finally
            {
                harness.GetSignal<QuorumRedeliveryOn43Command>().ThrowOnHandle = null;
                await harness.DisposeAsync();
            }
        }

        // Shared body for the quorum and classic max-receives facts: a handler that always throws must drive the
        // message to the dead-letter queue once MaxReceiveAttempts is exhausted, with the Receiver's own failure
        // headers merged onto the republished envelope.
        private async Task RunMaxReceivesScenarioAsync<TMessage>(QueueType queueType, Func<TMessage> commandFactory)
            where TMessage : class, ICommand
        {
            var set = RabbitMqTopology.CreateSet("v43_maxreceives", queueType);
            await RabbitMqTopology.DeclareAsync(_fixture.GetAmqpConnectionString(), set, CancellationToken.None);

            var harness = ChatterRabbitMqPipelineHarness.Build(
                _fixture.GetAmqpConnectionString(),
                queueType,
                rmq => rmq.AddQueueReceiver<TMessage>(
                    set.WorkQueueName,
                    deadLetterQueuePath: set.DeadLetterQueueName,
                    maxReceiveAttempts: MaxReceiveAttempts),
                typeof(TMessage));
            try
            {
                await harness.StartAsync();

                harness.GetSignal<TMessage>().ThrowOnHandle =
                    () => new InvalidOperationException("v43-maxreceives-test forced throw");

                await harness.SendToQueueAsync(commandFactory(), set.WorkQueueName);

                await harness.WaitForHandledAsync<TMessage>(DeadLetterWait);

                var deadLettered = await DeadLetterQueueReader.ReceiveAsync(
                    _fixture.GetAmqpConnectionString(), set.DeadLetterQueueName, DeadLetterWait);

                deadLettered.Should().NotBeNull(
                    $"a handler that always throws must exhaust maxReceiveAttempts ({MaxReceiveAttempts}) and be " +
                    $"republished to the dead-letter queue within {DeadLetterWait}");

                deadLettered.Headers.Should().ContainKey(MessageContext.FailureDetails,
                    "the Receiver's dead-letter republish merges FailureDetails, which a broker-side drop or " +
                    "dead-letter would not carry");

                deadLettered.Headers.Should().ContainKey(MessageContext.FailureDescription);
                deadLettered.Headers[MessageContext.FailureDescription]
                    .Should().Contain("v43-maxreceives-test forced throw",
                        "the dead-letter comes from the handler-failure path, not the poison path");
            }
            finally
            {
                var signal = harness.GetSignal<TMessage>();
                signal.ThrowOnHandle = null;

                // Written here rather than before the assertions because DeadLetterQueueReader can throw
                // TaskCanceledException at its deadline instead of returning null.
                _output.WriteLine($"{queueType} queue: handler invoked {signal.InvocationCount} time(s).");
                WriteDeliveredAttempts(signal.Records.Select(record => record.Context));

                await harness.DisposeAsync();
            }
        }

        // Writes the ReceiveAttempts the Receiver resolved and the broker's redelivered flag for the first few
        // deliveries. Diagnostic only: no assertion depends on it.
        private void WriteDeliveredAttempts(IEnumerable<IMessageBrokerContext> contexts)
        {
            var deliveryNumber = 0;
            foreach (var context in contexts.Take(DeliverySnapshotLimit))
            {
                deliveryNumber++;
                if (context is null || !context.Container.TryGet<ReceivedMessage>(out var received))
                {
                    _output.WriteLine($"delivery {deliveryNumber}: no ReceivedMessage in the broker context");
                    continue;
                }

                _output.WriteLine(
                    $"delivery {deliveryNumber}: redelivered={received.Redelivered} " +
                    $"{MessageContext.ReceiveAttempts}={DescribeReceiveAttempts(context)}");
            }
        }

        private static string DescribeReceiveAttempts(IMessageBrokerContext context)
        {
            var messageContext = context.BrokeredMessage?.MessageContext;
            if (messageContext is null || !messageContext.TryGetValue(MessageContext.ReceiveAttempts, out var value))
            {
                return "absent";
            }

            return value is null ? "null" : $"{value}";
        }
    }
}
