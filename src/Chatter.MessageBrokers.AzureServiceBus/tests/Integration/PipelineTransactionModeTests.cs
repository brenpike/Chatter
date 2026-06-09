using System;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Integration
{
    // Transaction-mode behavior of Chatter's Azure Service Bus receive path, driven entirely through
    // Chatter: messages are sent via IBrokeredMessageDispatcher and received by Chatter's pump into a
    // RecordingMessageHandler. Assertions read the handler invocation count Chatter produced; raw Azure SDK
    // appears ONLY at the test edge to confirm a queue holds nothing extra (peek for emptiness), never as the
    // thing under test.
    //
    // The observable distinction:
    //   TransactionMode.ReceiveOnly -> PeekLock + Complete. A succeeding handler settles the message (no
    //     redelivery); a throwing handler abandons it, so Chatter redelivers until max attempts (count climbs).
    //   TransactionMode.None -> ReceiveAndDelete. The message is removed on receipt and ack is a no-op, so a
    //     throwing handler LOSES the message with no redelivery (count stays at one).
    //
    // All facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineTransactionModeTests
    {
        private const string ReceiveOnlyQueue = "chatter.receiveonly";
        private const string NoneQueue = "chatter.none";
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);
        // A window long enough that a redelivery (if it were going to happen) would land within it, used to
        // assert the ABSENCE of further invocations in the None/no-redelivery cases.
        private static readonly TimeSpan NoRedeliveryWindow = TimeSpan.FromSeconds(10);

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineTransactionModeTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        public sealed class ReceiveOnlyCommand : ICommand
        {
            public string Value { get; set; }
        }

        public sealed class NoneCommand : ICommand
        {
            public string Value { get; set; }
        }

        // Raw-SDK edge helper: drains the queue with ReceiveAndDelete and asserts nothing remains. This is the
        // only place the Azure SDK is used, and only to confirm emptiness — never to receive the message under
        // test. ReceiveAndDelete (not PeekLock) removes whatever it finds so a leftover cannot reappear after a
        // lock expiry and leak into a later test.
        private async Task AssertQueueEmptyAsync(string queue)
        {
            await using var client = new ServiceBusClient(_emulator.GetConnectionString());
            var receiver = client.CreateReceiver(queue, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
            });

            var leftover = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            leftover.Should().BeNull($"queue '{queue}' must hold nothing extra after the pipeline settles the message");
        }

        // Bounded drain: ReceiveAndDelete-drains the active queue until it yields nothing within a short window,
        // bounded by an overall timeout. Used to WAIT for an in-flight settle/deadletter to finish (the active
        // copy of the message disappears once Chatter abandons-to-deadletter at max attempts) so the next test
        // on the SAME shared queue cannot consume a leftover that was still settling at teardown. Bounded so it
        // never hangs CI.
        private async Task DrainQueueAsync(string queue, TimeSpan timeout)
        {
            await using var client = new ServiceBusClient(_emulator.GetConnectionString());
            var receiver = client.CreateReceiver(queue, new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
            });

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var leftover = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
                if (leftover is null)
                {
                    return;
                }
            }
        }

        // ReceiveOnly happy path: a command sent through Chatter is handled exactly once and settled
        // (Complete). No redelivery occurs within a bounded window, and the queue holds nothing extra.
        [RequiresDockerFact]
        public async Task ReceiveOnlySettlesMessageAfterSingleSuccessfulHandling()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<ReceiveOnlyCommand>(ReceiveOnlyQueue, transactionMode: TransactionMode.ReceiveOnly),
                typeof(ReceiveOnlyCommand));
            await harness.StartAsync();

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new ReceiveOnlyCommand { Value = "receive-only" }, ReceiveOnlyQueue);
            }

            var handled = await harness.WaitForHandledAsync<ReceiveOnlyCommand>(HandlerWait);
            handled.Message.Value.Should().Be("receive-only");

            // No redelivery: the count must stay at one across the no-redelivery window (Complete settled it).
            var observed = await harness.WaitForInvocationCountAsync<ReceiveOnlyCommand>(2, NoRedeliveryWindow);
            observed.Should().Be(1, "a successfully handled PeekLock message is completed and never redelivered");

            await AssertQueueEmptyAsync(ReceiveOnlyQueue);
        }

        // None vs ReceiveOnly, throwing handler — None branch: under ReceiveAndDelete the message is gone on
        // receipt and ack is a no-op, so a throwing handler loses the message with NO redelivery. The handler
        // is invoked exactly once and the queue is empty afterward (the message was deleted on receive).
        [RequiresDockerFact]
        public async Task NoneLosesMessageWithoutRedeliveryWhenHandlerThrows()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<NoneCommand>(NoneQueue, transactionMode: TransactionMode.None),
                typeof(NoneCommand));
            await harness.StartAsync();

            harness.GetSignal<NoneCommand>().ThrowOnHandle =
                () => new InvalidOperationException("force handler failure on a None (ReceiveAndDelete) receiver");

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new NoneCommand { Value = "none-lost" }, NoneQueue);
            }

            var handled = await harness.WaitForHandledAsync<NoneCommand>(HandlerWait);
            handled.Message.Value.Should().Be("none-lost");

            // No redelivery under ReceiveAndDelete: the count stays at one even though the handler threw.
            var observed = await harness.WaitForInvocationCountAsync<NoneCommand>(2, NoRedeliveryWindow);
            observed.Should().Be(1, "a None receiver deletes on receipt, so a thrown handler does not get a redelivery");

            // The message was removed on receive (ReceiveAndDelete), so nothing remains to redeliver.
            await AssertQueueEmptyAsync(NoneQueue);
        }

        // None vs ReceiveOnly, throwing handler — ReceiveOnly branch: under PeekLock a throwing handler
        // abandons the lock, so Chatter REDELIVERS the message. With maxReceiveAttempts of 2 the handler is
        // invoked more than once before Chatter deadletters it, proving the settle-difference versus None.
        [RequiresDockerFact]
        public async Task ReceiveOnlyRedeliversMessageWhenHandlerThrows()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<ReceiveOnlyCommand>(ReceiveOnlyQueue, transactionMode: TransactionMode.ReceiveOnly, maxReceiveAttempts: 2),
                typeof(ReceiveOnlyCommand));
            await harness.StartAsync();

            harness.GetSignal<ReceiveOnlyCommand>().ThrowOnHandle =
                () => new InvalidOperationException("force handler failure on a ReceiveOnly (PeekLock) receiver");

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new ReceiveOnlyCommand { Value = "receive-only-redelivered" }, ReceiveOnlyQueue);
            }

            await harness.WaitForHandledAsync<ReceiveOnlyCommand>(HandlerWait);

            // PeekLock redelivery: a throwing handler abandons the lock, so the message is delivered again
            // (count climbs past the first invocation) before Chatter deadletters at max attempts.
            var observed = await harness.WaitForInvocationCountAsync<ReceiveOnlyCommand>(2, HandlerWait);
            observed.Should().BeGreaterThanOrEqualTo(2, "a PeekLock receiver redelivers an abandoned message");

            // Settlement happens AFTER the second invocation is observed: the throwing handler unwinds and
            // Chatter abandons-to-deadletter at maxReceiveAttempts. Disposing the harness right here would
            // cancel the receiver mid-settlement and could leave 'receive-only-redelivered' locked/abandoned on
            // the SHARED chatter.receiveonly queue, where a later test on the same queue could consume it and
            // fail nondeterministically. Drain the active queue (bounded) so any in-flight redelivery/settle is
            // consumed before teardown, leaving nothing active for the next test.
            await DrainQueueAsync(ReceiveOnlyQueue, HandlerWait);
        }
    }
}
