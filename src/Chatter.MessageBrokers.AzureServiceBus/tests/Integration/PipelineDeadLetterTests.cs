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
    // Deadletter coverage driven THROUGH Chatter's pipeline. The SYSTEM UNDER TEST is Chatter's receive +
    // deadletter path: a command is sent via IBrokeredMessageDispatcher.Send, Chatter's pump delivers it to a
    // RecordingMessageHandler whose handler THROWS, and — because the receiver is registered with
    // maxReceiveAttempts: 1 — Chatter's BrokeredMessageReceiver loop deadletters the message on the first
    // failed delivery (deliveryCount >= MaxReceiveAttempts). The proof that Chatter deadlettered it is read
    // from the entity's $DeadLetterQueue sub-queue.
    //
    // Raw Azure.Messaging.ServiceBus appears ONLY at the test EDGE to PEEK the dead-letter sub-queue
    // (chatter.deadletter/$DeadLetterQueue), which Chatter does not expose for reads — never as the system
    // under test. The send and the receive/throw/deadletter are entirely Chatter's pipeline.
    //
    // Deadletter reason/description provenance (read from BrokeredMessageReceiver.TryDeadletterWithRecoveryAsync
    // and ServiceBusReceiver.DeadletterMessageAsync):
    //   reason       => the literal "Poisoned message received"
    //   description  => the throwing handler's exception, rendered via Exception.ToString() (type + message +
    //                   stack trace), then capped by ServiceBusReceiver.CapDeadLetterErrorDescription to 4096
    //                   UTF-16 chars ending in the "…[truncated]" marker (issue #92).
    //
    // All facts are gated by [RequiresDockerFact] and SKIPPED (never failed) when Docker is absent so a plain
    // `dotnet test` stays green; the emulator CI lane (`--filter Category=Integration`) runs them for real.
    [Trait("Category", "Integration")]
    [Collection(ServiceBusEmulatorCollection.Name)]
    public class PipelineDeadLetterTests
    {
        private const string DeadLetterQueue = "chatter.deadletter";
        // A dedicated queue (MaxDeliveryCount 10, so the broker does NOT deadletter before Chatter does) for the
        // exact-attempts boundary test: Chatter's maxReceiveAttempts (small N) governs deadlettering, and the
        // queue is isolated so its $DeadLetterQueue peek is not polluted by other tests' poisoned messages.
        private const string AttemptsQueue = "chatter.attempts";
        // The exact number of handler invocations expected on the attempts boundary test before Chatter
        // deadletters: Chatter deadletters when deliveryCount >= MaxReceiveAttempts, so with N attempts the
        // PeekLock handler is invoked exactly N times.
        private const int MaxReceiveAttemptsForBoundary = 2;
        private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(30);
        // A window long enough that any further (over-N) redelivery would have landed, used to assert the handler
        // is NOT invoked beyond the exact attempt count.
        private static readonly TimeSpan NoFurtherInvocationWindow = TimeSpan.FromSeconds(15);
        // A window long enough for Chatter to settle the deadletter after the handler throws, before the
        // edge-only peek of the $DeadLetterQueue sub-queue.
        private static readonly TimeSpan DeadLetterPeekWait = TimeSpan.FromSeconds(20);

        // The SDK-side marker/limit MUST mirror ServiceBusReceiver's private constants; this test asserts the
        // capped DeadLetterErrorDescription Chatter wrote to the DLQ, so the expected shape is duplicated here
        // (the production constants are private and not part of the public surface).
        private const int MaxDeadLetterErrorDescriptionLength = 4096;
        private const string DeadLetterErrorDescriptionTruncationMarker = "…[truncated]";

        private readonly ServiceBusEmulatorFixture _emulator;

        public PipelineDeadLetterTests(ServiceBusEmulatorFixture emulator)
            => _emulator = emulator;

        public sealed class DeadLetterCommand : ICommand
        {
            public string Value { get; set; }
        }

        // Edge-only raw-SDK read of the entity's dead-letter sub-queue. Chatter exposes no DLQ read, so the
        // Azure SDK is used here purely to observe the message Chatter's deadletter path produced. PeekLock +
        // explicit Complete drains it so a leftover cannot leak into a later run on the shared emulator.
        private async Task<ServiceBusReceivedMessage> ReceiveDeadLetteredAsync(TimeSpan timeout, string queue = DeadLetterQueue)
        {
            await using var client = new ServiceBusClient(_emulator.GetConnectionString());
            var receiver = client.CreateReceiver(
                queue,
                new ServiceBusReceiverOptions
                {
                    SubQueue = SubQueue.DeadLetter,
                    ReceiveMode = ServiceBusReceiveMode.PeekLock,
                });

            var deadLettered = await receiver.ReceiveMessageAsync(timeout);
            if (deadLettered != null)
            {
                await receiver.CompleteMessageAsync(deadLettered);
            }

            return deadLettered;
        }

        // A throwing handler on a PeekLock receiver with maxReceiveAttempts: 1 causes Chatter to deadletter the
        // message on its first failed delivery. The message lands on chatter.deadletter/$DeadLetterQueue with
        // the reason/description Chatter's deadletter path stamps.
        [RequiresDockerFact]
        public async Task ThrowingHandlerDeadlettersMessageThroughChatterPipeline()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<DeadLetterCommand>(
                    DeadLetterQueue,
                    transactionMode: TransactionMode.ReceiveOnly,
                    maxReceiveAttempts: 1),
                typeof(DeadLetterCommand));
            await harness.StartAsync();

            harness.GetSignal<DeadLetterCommand>().ThrowOnHandle =
                () => new InvalidOperationException("force deadletter through Chatter's pipeline");

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new DeadLetterCommand { Value = "to-deadletter" }, DeadLetterQueue);
            }

            // The handler WAS invoked (and threw) through Chatter's pipeline.
            await harness.WaitForHandledAsync<DeadLetterCommand>(HandlerWait);

            // Chatter deadlettered it: the message is on the entity's dead-letter sub-queue with Chatter's
            // reason. This DLQ read is the only raw-SDK usage and is an assertion-only edge read.
            var deadLettered = await ReceiveDeadLetteredAsync(DeadLetterPeekWait);

            deadLettered.Should().NotBeNull(
                "Chatter's deadletter path must move the message to the entity's $DeadLetterQueue after the handler throws at max attempts");
            deadLettered.DeadLetterReason.Should().Be(
                "Poisoned message received",
                "Chatter's BrokeredMessageReceiver stamps this literal reason when it deadletters a failed message");
            deadLettered.DeadLetterErrorDescription.Should().NotBeNullOrEmpty(
                "Chatter derives the deadletter description from the throwing handler's exception");
            deadLettered.DeadLetterErrorDescription.Should().Contain(
                "force deadletter through Chatter's pipeline",
                "the description is the handler exception rendered via Exception.ToString()");
        }

        // Issue #92: when the throwing handler's exception renders (via Exception.ToString()) to more than
        // 4096 UTF-16 chars, Chatter's ServiceBusReceiver.CapDeadLetterErrorDescription truncates the
        // DeadLetterErrorDescription to exactly 4096 chars ending in the "…[truncated]" marker so the SDK does
        // not reject the deadletter with ArgumentOutOfRangeException. The description on this path is derived
        // from the exception (BrokeredMessageReceiver passes e.ToString()), so an over-length exception message
        // exercises the cap directly.
        [RequiresDockerFact]
        public async Task OverlengthDeadLetterDescriptionIsCappedTo4096ByChatter()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<DeadLetterCommand>(
                    DeadLetterQueue,
                    transactionMode: TransactionMode.ReceiveOnly,
                    maxReceiveAttempts: 1),
                typeof(DeadLetterCommand));
            await harness.StartAsync();

            // An exception message guaranteed to push Exception.ToString() (type + message + stack trace) well
            // past the 4096-char cap, so the capped description is observable.
            var overlengthMessage = new string('X', MaxDeadLetterErrorDescriptionLength + 2048);
            harness.GetSignal<DeadLetterCommand>().ThrowOnHandle =
                () => new InvalidOperationException(overlengthMessage);

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new DeadLetterCommand { Value = "overlength-deadletter" }, DeadLetterQueue);
            }

            await harness.WaitForHandledAsync<DeadLetterCommand>(HandlerWait);

            var deadLettered = await ReceiveDeadLetteredAsync(DeadLetterPeekWait);

            deadLettered.Should().NotBeNull(
                "Chatter must deadletter the message even when the exception description is over-length");
            deadLettered.DeadLetterErrorDescription.Should().NotBeNull();
            deadLettered.DeadLetterErrorDescription.Length.Should().Be(
                MaxDeadLetterErrorDescriptionLength,
                "issue #92: Chatter caps the deadletter description at the SDK's 4096 UTF-16 char limit");
            deadLettered.DeadLetterErrorDescription.Should().EndWith(
                DeadLetterErrorDescriptionTruncationMarker,
                "the capped description preserves its diagnostic head and appends Chatter's truncation marker");
        }

        // Exact attempts -> deadletter boundary: with a throwing handler on a PeekLock receiver configured for
        // maxReceiveAttempts: N (on a queue whose own MaxDeliveryCount is higher, so the BROKER does not
        // deadletter first), Chatter invokes the handler EXACTLY N times — it redelivers on each abandoned lock
        // and deadletters only once deliveryCount >= N — and then the message lands on the entity's
        // $DeadLetterQueue. This pins the attempts->deadletter boundary precisely (the existing tests assert only
        // >= 2 redelivery); both the exact invocation count and the resulting DLQ landing are proven through
        // Chatter, with the raw SDK used only at the edge to peek the DLQ.
        [RequiresDockerFact]
        public async Task ThrowingHandlerIsInvokedExactlyMaxAttemptsThenDeadletters()
        {
            await using var harness = ChatterPipelineHarness.Build(
                _emulator.GetConnectionString(),
                sb => sb.AddQueueReceiver<DeadLetterCommand>(
                    AttemptsQueue,
                    transactionMode: TransactionMode.ReceiveOnly,
                    maxReceiveAttempts: MaxReceiveAttemptsForBoundary),
                typeof(DeadLetterCommand));
            await harness.StartAsync();

            harness.GetSignal<DeadLetterCommand>().ThrowOnHandle =
                () => new InvalidOperationException("force exact-attempts deadletter through Chatter's pipeline");

            var dispatcher = harness.CreateDispatcher(out var scope);
            using (scope)
            {
                await dispatcher.Send(new DeadLetterCommand { Value = "exact-attempts" }, AttemptsQueue);
            }

            // Chatter invokes the handler on each PeekLock delivery until it deadletters at deliveryCount >= N,
            // so the handler is invoked exactly N times: first reach N...
            var reached = await harness.WaitForInvocationCountAsync<DeadLetterCommand>(
                MaxReceiveAttemptsForBoundary, HandlerWait);
            reached.Should().Be(
                MaxReceiveAttemptsForBoundary,
                "Chatter redelivers an abandoned PeekLock message and deadletters at deliveryCount >= maxReceiveAttempts, so the handler is invoked exactly that many times");

            // ...then confirm it does NOT exceed N within a margin (no further redelivery after deadlettering).
            var afterDeadletter = await harness.WaitForInvocationCountAsync<DeadLetterCommand>(
                MaxReceiveAttemptsForBoundary + 1, NoFurtherInvocationWindow);
            afterDeadletter.Should().Be(
                MaxReceiveAttemptsForBoundary,
                "once Chatter deadletters at max attempts the message is gone, so the handler is not invoked again");

            // The message then lands on the entity's $DeadLetterQueue — the attempts boundary terminated in a
            // deadletter, not an endless redelivery. This DLQ read is the only raw-SDK usage and drains the
            // message so it cannot leak into a later run on the shared emulator.
            var deadLettered = await ReceiveDeadLetteredAsync(DeadLetterPeekWait, AttemptsQueue);
            deadLettered.Should().NotBeNull(
                "after exactly maxReceiveAttempts failed deliveries Chatter must move the message to the entity's $DeadLetterQueue");
            deadLettered.DeadLetterReason.Should().Be(
                "Poisoned message received",
                "Chatter stamps this literal reason when it deadletters at the attempts boundary");
        }
    }
}
