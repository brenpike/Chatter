using System;
using System.Threading.Tasks;
using Chatter.CQRS.Commands;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Integration
{
    // Dialog-lifecycle and bounded-timeout/cancellation teardown proofs for the SQL Service Broker integration
    // harness (STEP-008). Two orthogonal concerns:
    //
    //   Test A — EndDialog auto-ack tolerance: proves the EndDialog system message that SSB delivers after
    //   SqlServiceBrokerReceiver.AckMessageAsync commits the RECEIVE transaction is classified by
    //   ServiceBrokerMessageClassifier as ClassificationOutcome.EndDialog, routed to AckEndDialogAsync (auto-acked
    //   and committed), and never forwarded to Chatter's dispatcher — so InvocationCount stays at exactly 1.
    //
    //   Test B — Bounded teardown / WAITFOR-hang guard: proves that a harness with a finite
    //   ReceiverTimeoutInMilliseconds against an EMPTY queue (WAITFOR RECEIVE ... TIMEOUT 5 returns an empty row
    //   set promptly and loops on the pump CancellationToken) tears down within a bounded wall-clock window.
    //   DisposeAsync cancels the pump CTS then stops the host; a blocked/looping RECEIVE unwinds via token
    //   cancellation. The test wraps DisposeAsync in Task.WhenAny vs Task.Delay(30 s) and asserts the dispose
    //   task won, proving no WAITFOR hang.
    //
    // Both facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green. Mirrors SsbRoundTripTests for collection membership.
    [Trait("Category", "Integration")]
    [Collection(SqlServiceBrokerCollection.Name)]
    public class SsbDialogLifecycleTests
    {
        // How long to wait for the command handler to be invoked.
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);

        // After the command is handled, how long to let the pump loop before asserting InvocationCount is still 1.
        // The EndDialog system message arrives on the same queue within milliseconds; a brief settle is enough.
        private static readonly TimeSpan EndDialogSettleWait = TimeSpan.FromSeconds(3);

        // Maximum wall-clock window for DisposeAsync to complete on a harness whose queue is empty. The pump loops
        // on a 5 ms WAITFOR, so cancellation unwinds within one loop iteration; 30 s is a generous CI bound.
        private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(30);

        private readonly SqlServiceBrokerFixture _fixture;

        public SsbDialogLifecycleTests(SqlServiceBrokerFixture fixture)
            => _fixture = fixture;

        // Distinct command type so this test class's queue state is independent of the other integration test
        // classes sharing the same SqlServiceBroker collection.
        public sealed class DialogLifecycleCommand : ICommand
        {
            public string Marker { get; set; }
        }

        private ChatterSsbPipelineHarness BuildHarness()
            => ChatterSsbPipelineHarness.Build(
                _fixture.GetAppConnectionString(),
                ssb => ssb.AddQueueReceiver<DialogLifecycleCommand>(
                    ServiceBrokerProvisioning.TargetQueuePathBracketed,
                    deadLetterServicePath: ServiceBrokerProvisioning.DeadLetterServiceName),
                typeof(DialogLifecycleCommand));

        // Test A — dialog begin/end + EndDialog auto-ack tolerance.
        //
        // Dispatch one command and wait for the handler. After the handler invocation Chatter's ACK path
        // (SqlServiceBrokerReceiver.AckMessageAsync) commits and issues END CONVERSATION, which causes SSB to
        // deliver an EndDialog system message back onto the same queue. ServiceBrokerMessageClassifier classifies
        // that message as ClassificationOutcome.EndDialog; the receiver routes it to AckEndDialogAsync (auto-acked,
        // never dispatched to Chatter's handler pipeline). Assert:
        //   (a) the round-trip succeeds — the handler is invoked with the correct payload;
        //   (b) InvocationCount == 1 immediately after the handler wait (EndDialog not yet received); and
        //   (c) InvocationCount is STILL 1 after a bounded settle, proving the EndDialog auto-ack path does not
        //       forward the system message to the handler.
        [RequiresDockerFact]
        public async Task EndDialogSystemMessageIsAutoAckedAndDoesNotReachHandler()
        {
            var harness = BuildHarness();
            try
            {
                await harness.StartAsync();

                var sent = new DialogLifecycleCommand { Marker = "dialog-lifecycle" };
                await harness.SendAsync(sent);

                var handled = await harness.WaitForHandledAsync<DialogLifecycleCommand>(HandlerWait);

                // (a) The round trip completed: the handler received the command with its payload intact.
                handled.Message.Should().NotBeNull(
                    "Chatter's SSB pipeline must deliver the command to the handler");
                handled.Message.Marker.Should().Be("dialog-lifecycle");

                // (b) Immediately after the handler wait InvocationCount must be exactly 1.
                harness.GetSignal<DialogLifecycleCommand>().InvocationCount
                    .Should().Be(1,
                        "only the command dispatch should have reached the handler at this point");

                // (c) Let the pump loop for a bounded window so the EndDialog system message has time to arrive
                // and be processed. InvocationCount must remain at 1: the classifier routes EndDialog system
                // messages to AckEndDialogAsync (auto-acked + committed) and returns null, so ReceiveMessageAsync
                // returns null and the dispatcher is never invoked.
                await Task.Delay(EndDialogSettleWait).ConfigureAwait(false);

                harness.GetSignal<DialogLifecycleCommand>().InvocationCount
                    .Should().Be(1,
                        "the EndDialog system message must be auto-acked by the classifier and must NOT reach " +
                        "the handler; InvocationCount must stay at 1 after the settle window");
            }
            finally
            {
                await harness.DisposeAsync();
            }
        }

        // Test B — bounded-timeout / cancellation teardown (the WAITFOR-hang guard).
        //
        // Build and start a harness with a receiver but dispatch NOTHING. The queue stays empty, so each pump
        // iteration issues WAITFOR(RECEIVE ... TIMEOUT 5) which returns an empty row set quickly and re-enters
        // the loop on the pump CancellationToken. DisposeAsync cancels the pump CTS then stops the hosted
        // services; token cancellation is the only signal that unwinds a blocked/looping RECEIVE
        // (StopReceiver()/Cancel() are no-ops).
        //
        // The bounded-teardown assertion: wrap DisposeAsync in Task.WhenAny vs Task.Delay(TeardownBound). Assert
        // that the dispose task won (not the delay). A WAITFOR hang would cause the delay to win, failing the test.
        // DisposeAsync is called exactly once here; the finally block is a no-op after the assertion handles disposal.
        [RequiresDockerFact]
        public async Task DisposeAsyncCompletesWithinBoundedTimeOnEmptyQueue()
        {
            var harness = BuildHarness();
            var disposeTask = (ValueTask?)null;
            try
            {
                await harness.StartAsync();

                // Dispatch nothing — the queue stays empty and the pump loops on WAITFOR ... TIMEOUT 5 until
                // the pump CTS is cancelled by DisposeAsync.
                var disposeValueTask = harness.DisposeAsync();
                disposeTask = disposeValueTask;
                var disposeAsTask = disposeValueTask.AsTask();
                var boundTask = Task.Delay(TeardownBound);

                var winner = await Task.WhenAny(disposeAsTask, boundTask).ConfigureAwait(false);

                winner.Should().BeSameAs(disposeAsTask,
                    $"DisposeAsync must complete within {TeardownBound.TotalSeconds} s on an empty queue; " +
                    "a WAITFOR hang means the pump CTS cancellation did not unwind the RECEIVE loop");

                // Surface any exception from DisposeAsync now that we know it completed.
                await disposeAsTask.ConfigureAwait(false);
            }
            finally
            {
                // DisposeAsync was already awaited above when it won. If the assert threw before await (i.e. the
                // delay won), we still need to drain the harness to avoid resource leaks; best-effort only.
                if (disposeTask == null)
                {
                    await harness.DisposeAsync();
                }
            }
        }
    }
}
